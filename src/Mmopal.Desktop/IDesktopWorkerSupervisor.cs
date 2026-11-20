using Mmopal.Contracts;

namespace Mmopal.Desktop;

internal interface IDesktopWorkerSupervisor : IAsyncDisposable
{
    event EventHandler<WorkerEvent>? EventReceived;

    Task<WorkerEvent> SendAsync(WorkerCommand command, CancellationToken cancellationToken = default);
}
