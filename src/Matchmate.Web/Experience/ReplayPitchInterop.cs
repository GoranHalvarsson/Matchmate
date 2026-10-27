using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Matchmate.Web.Experience;

public sealed class ReplayPitchInterop(IJSRuntime js) : IAsyncDisposable
{
    internal const string ModulePath = "./Components/Pages/FootballReplay.razor.js";
    internal const string FocusMethod = "focusPitch";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IJSObjectReference? _module;
    private int _disposed;
    public async ValueTask FocusAsync(ElementReference pitch)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        await _gate.WaitAsync();
        try
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            // Serialize import/focus/disposal so concurrent first uses cannot leak a second module.
            _module ??= await js.InvokeAsync<IJSObjectReference>("import", ModulePath);
            if (Volatile.Read(ref _disposed) != 0)
            {
                var lateModule = _module; _module = null;
                await lateModule.DisposeAsync();
                return;
            }
            await _module.InvokeVoidAsync(FocusMethod, pitch);
        }
        catch (JSDisconnectedException) { }
        finally { _gate.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        // Mark immediately, including while import is suspended. Every disposer awaits the same serialized cleanup.
        Interlocked.Exchange(ref _disposed, 1);
        await _gate.WaitAsync();
        try
        {
            var module = _module; _module = null;
            if (module is not null) await module.DisposeAsync();
        }
        catch (JSDisconnectedException) { }
        finally { _gate.Release(); }
    }
}
