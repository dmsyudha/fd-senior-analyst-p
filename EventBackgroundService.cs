using CatalystGames.Services.Data;
using CatalystGames.Services.Data.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace CatalystGames.Services.Background;

public class PeriodicEventBackgroundService : BackgroundService
{
    private readonly IConfiguration _configuration;
	private readonly IServiceScopeFactory _serviceScopeFactory;

    public PeriodicEventBackgroundService(
        IConfiguration configuration,
		IServiceScopeFactory serviceScopeFactory)
    {
        _configuration = configuration;
		_serviceScopeFactory = serviceScopeFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Get the settings how often we need check if an event needs to be set to completed
        _ = double.TryParse(_configuration["PeriodicTaskSettings:Events:CheckInterval"], out double period);
        _ = double.TryParse(_configuration["PeriodicTaskSettings:Events:CompletedAfterHours"], out double completedAfterHours);

		// Create periodic timer for scheduling task
		using PeriodicTimer timer = new(TimeSpan.FromSeconds(period));
        while (
            !stoppingToken.IsCancellationRequested &&
            await timer.WaitForNextTickAsync(stoppingToken))
        {
            Log.Information($"PeriodicEventBackgroundService executed at {DateTime.UtcNow}");
            try
            {
				// Create the Async Scope factory
				await using AsyncServiceScope asyncScope = _serviceScopeFactory.CreateAsyncScope();

				// Get all incompleted events from the database
                // The incomplete event should be the one that New and Running state
				var eventDataService = asyncScope.ServiceProvider.GetRequiredService<IEventDataService>();
                var allEvents = await eventDataService.GetAllAsync(cancellationToken: stoppingToken);
				var incompletedEvents = allEvents.Where(e => e.EventStatusId == EventStatuses.New || e.EventStatusId == EventStatuses.Running);

                // Loop through all incomplete event and check the end date has passed the completedAfterHours
                foreach (var incompleteEvent in incompletedEvents.ToList())
                {
                    // Get all the playlist of the event
                    var eventPlaylist = await eventDataService.GetPlaylistsAsync(incompleteEvent.Id, new PageRequest(), cancellationToken: stoppingToken);

                    // Assume that by default we have to update the parent Event
                    bool updateEventParent = true;

                    // Loop through all event palylist item
                    foreach (var playlistItem in eventPlaylist)
                    {
                        // Only process the checking if the playlist status is new or running
                        if (playlistItem.EventStatusId == EventStatuses.New || playlistItem.EventStatusId == EventStatuses.Running)
                        {
                            // Check if the playlist end date has passed the completedAfterHours
                            if (playlistItem.EndDateUtc.HasValue && DateTime.UtcNow >= playlistItem.EndDateUtc?.AddHours(completedAfterHours))
                            {
                                // Update the status of the playlist to completed
                                await eventDataService.PatchEventPlaylistBySystemAsync(playlistItem.Id, new PatchEventPlaylistBySystem { Status = EventStatuses.Completed }, stoppingToken);
                            }
                            else
                            {
                                // The event may don't have end date or not passed the end date yet
                                updateEventParent = false;
                            }
                        }
                    }

                    // Need to update the parent event as all the playlist have completed
                    if (updateEventParent)
                    {
                        // Update the parent event only if all playlist are completed
                        await eventDataService.PatchEventBySystemAsync(incompleteEvent.Id, new PatchEventBySystem { Status = EventStatuses.Completed }, stoppingToken);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to execute PeriodicEventBackgroundService with exception message {ex.Message}.");
            }
        }
    }

    public override async Task StopAsync(CancellationToken stoppingToken)
    {
        Log.Information("Periodic event check service is stopping.");
        await base.StopAsync(stoppingToken);
    }

    public async Task<IEnumerable<EventListItem>> GetAllAsync(bool includeDeleted = false, CancellationToken cancellationToken = default)
	=> (await _dbContext.Event
		.Where(e => includeDeleted || !e.IsDeleted)
		.WithPath(wp => wp
				.Prefetch<EventPlaylistEntity>(playlist => playlist.EventPlaylists).FilterOn(pl => !pl.IsDeleted))
		.ToArrayAsync(cancellationToken))
		.MapTo<EventListItem>(_mapper)
		.ToArray();

    public async Task<EventPlaylist> PatchEventPlaylistBySystemAsync(int eventPlaylistId, PatchEventPlaylistBySystem model, CancellationToken cancellationToken = default)
    {
        // validate model
        await model.ValidateAsync<PatchEventPlaylistBySystem.Validator>(cancellationToken);

        // check entity exists
        var eventPlaylist = await GetEventPlaylistAsync(eventPlaylistId, cancellationToken);

        // Map the existing entity to the Patch model
        var map = _mapper.Map<PatchEventPlaylistBySystem>(eventPlaylist, o =>
        {
            o.AfterMap((src, des) =>
            {
                des.Status = model.Status;
            });
        });

        // create the entity to update
        var entity = _mapper.Map<EventPlaylistEntity>(map, o =>
        {
            o.AfterMap((src, des) =>
            {
                des.Id = eventPlaylistId;
                des.IsNew = false;
            });
        });

        // save changes
        if (entity.IsDirty)
        {
            await _dbContext.AdapterToUse.SaveEntityAsync(entity, true, cancellationToken);
            await _dbContext.AdapterToUse.CommitAsync(cancellationToken);
        }

        return _mapper.Map<EventPlaylist>(entity);
    }
}




			