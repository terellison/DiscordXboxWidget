using Windows.ApplicationModel;
using Windows.ApplicationModel.AppService;
using Windows.Foundation.Collections;

namespace Discord.Rpc.Bridge;

/// <summary>
/// Connects the bridge to the widget's AppService and pumps messages both ways.
/// </summary>
/// <remarks>
/// The widget hosts the service and the bridge is the client, which is the direction the
/// full-trust launch flow implies: the widget starts us, then we call back in.
/// </remarks>
internal sealed class AppServiceBridge : IDisposable
{
    private readonly BridgeHost _host;
    private readonly TaskCompletionSource<object?> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private AppServiceConnection? _connection;

    public AppServiceBridge(BridgeHost host)
    {
        _host = host;
        _host.EventRaised += OnHostEvent;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _connection = new AppServiceConnection
        {
            AppServiceName = BridgeProtocol.AppServiceName,
            // Package.Current requires package identity, which we only have when launched
            // through FullTrustProcessLauncher from the packaged widget.
            PackageFamilyName = Package.Current.Id.FamilyName,
        };

        _connection.RequestReceived += OnRequestReceived;
        _connection.ServiceClosed += (_, _) => _closed.TrySetResult(null);

        var status = await _connection.OpenAsync();
        if (status != AppServiceConnectionStatus.Success)
            throw new InvalidOperationException($"Could not open AppService '{BridgeProtocol.AppServiceName}': {status}");

        // Connect to Discord only after the channel to the widget exists, so the state and
        // channel events raised during connect are not dropped on the floor.
        await _host.ConnectAsync(cancellationToken).ConfigureAwait(false);

        using (cancellationToken.Register(() => _closed.TrySetResult(null)))
        {
            await _closed.Task.ConfigureAwait(false);
        }
    }

    private async void OnRequestReceived(AppServiceConnection sender, AppServiceRequestReceivedEventArgs args)
    {
        // The deferral must be taken before the first await or the request is considered
        // complete and the ValueSet is torn down underneath us.
        var deferral = args.GetDeferral();
        var response = new ValueSet();

        try
        {
            var message = args.Request.Message;
            var command = message.TryGetValue(BridgeProtocol.KeyCommand, out var c) ? c as string : null;
            var requestId = message.TryGetValue(BridgeProtocol.KeyRequestId, out var i) ? i : null;

            if (requestId != null) response[BridgeProtocol.KeyRequestId] = requestId;

            if (string.IsNullOrEmpty(command))
            {
                response[BridgeProtocol.KeySuccess] = false;
                response[BridgeProtocol.KeyError] = "Message had no command.";
            }
            else
            {
                var stringArg = message.TryGetValue(BridgeProtocol.ArgChannelId, out var s) ? s as string : null;
                var boolArg = message.TryGetValue(BridgeProtocol.ArgValue, out var b) && b is bool flag && flag;

                var payload = await _host.ExecuteAsync(command!, stringArg, boolArg, CancellationToken.None);

                response[BridgeProtocol.KeySuccess] = true;
                response[BridgeProtocol.KeyPayload] = payload;
            }
        }
        catch (Exception ex)
        {
            response[BridgeProtocol.KeySuccess] = false;
            response[BridgeProtocol.KeyError] = ex.Message;
        }
        finally
        {
            try { await args.Request.SendResponseAsync(response); } catch { /* widget went away */ }
            deferral.Complete();
        }
    }

    private async void OnHostEvent(string eventName, string payload)
    {
        var connection = _connection;
        if (connection == null) return;

        try
        {
            await connection.SendMessageAsync(new ValueSet
            {
                [BridgeProtocol.KeyEvent] = eventName,
                [BridgeProtocol.KeyPayload] = payload,
            });
        }
        catch
        {
            // Widget closed mid-push. Events are advisory; the widget re-reads on attach.
        }
    }

    public void Dispose()
    {
        _host.EventRaised -= OnHostEvent;
        _connection?.Dispose();
        _connection = null;
    }
}
