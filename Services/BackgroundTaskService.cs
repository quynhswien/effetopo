using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace effetopo.Services
{
    /// <summary>
    /// Service for managing background tasks
    /// </summary>
    public class BackgroundTaskService
    {
        private static BackgroundTaskService _instance;
        private static readonly object _lock = new object();

        // List to keep track of background tasks
        private readonly List<Task> _backgroundTasks = new List<Task>();
        private CancellationTokenSource _cancellationTokenSource;

        /// <summary>
        /// Singleton instance
        /// </summary>
        public static BackgroundTaskService Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                        {
                            _instance = new BackgroundTaskService();
                        }
                    }
                }
                return _instance;
            }
        }

        /// <summary>
        /// Initialize the background task service
        /// </summary>
        private BackgroundTaskService()
        {
            _cancellationTokenSource = new CancellationTokenSource();
            Log.Debug("BackgroundTaskService initialized");
        }

        /// <summary>
        /// Runs a task in the background and keeps track of it
        /// </summary>
        /// <param name="taskFunc">The task function to run</param>
        public void RunBackgroundTask(Func<CancellationToken, Task> taskFunc)
        {
            try
            {
                var token = _cancellationTokenSource.Token;
                var task = Task.Run(async () =>
                {
                    try
                    {
                        await taskFunc(token);
                    }
                    catch (TaskCanceledException)
                    {
                        Log.Debug("Background task was canceled");
                    }
                    catch (OperationCanceledException)
                    {
                        Log.Debug("Background task operation was canceled");
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Error in background task");
                        StatisticsCollectorService.Instance.RecordError("BackgroundTaskError", ex.Message, ex.StackTrace);
                    }
                }, token);

                lock (_backgroundTasks)
                {
                    _backgroundTasks.Add(task);
                }

                // Clean up completed tasks
                CleanupCompletedTasks();

                Log.Debug("Background task started");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error starting background task");
            }
        }

        /// <summary>
        /// Removes completed tasks from the tracking list
        /// </summary>
        private void CleanupCompletedTasks()
        {
            lock (_backgroundTasks)
            {
                for (int i = _backgroundTasks.Count - 1; i >= 0; i--)
                {
                    if (_backgroundTasks[i].IsCompleted)
                    {
                        _backgroundTasks.RemoveAt(i);
                    }
                }
            }
        }

        /// <summary>
        /// Cancels all background tasks and cleans up resources
        /// </summary>
        public void CancelAllTasks()
        {
            try
            {
                _cancellationTokenSource?.Cancel();

                // Wait for all tasks to complete with a timeout
                Task.WaitAll(_backgroundTasks.ToArray(), 2000);

                Log.Information("All background tasks canceled");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error canceling background tasks");
            }
        }
    }
}