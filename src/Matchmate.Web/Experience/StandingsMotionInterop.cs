using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Matchmate.Web.Experience;

// One observer per mounted table; no data, ordering or per-frame calls cross interop.
public sealed class StandingsMotionInterop(IJSRuntime js) : IAsyncDisposable
{
    internal const string ModulePath = "./Components/League/LeagueStandings.razor.js";
    internal const string InitializeMethod = "initialize", DisposeMethod = "dispose";
    private IJSObjectReference? _module;
    private ElementReference _body;
    private bool _disposed;
    public async ValueTask InitializeAsync(ElementReference body)
    {
        if (_disposed) return;
        try
        {
            var module = await js.InvokeAsync<IJSObjectReference>("import", ModulePath);
            if (_disposed) { await module.DisposeAsync(); return; }
            _module = module; _body = body;
            await module.InvokeVoidAsync(InitializeMethod, body);
        }
        catch (JSDisconnectedException) { }
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return; _disposed = true;
        var module = _module; _module = null;
        if (module is null) return;
        try { await module.InvokeVoidAsync(DisposeMethod, _body); }
        catch (JSDisconnectedException) { }
        finally
        {
            try { await module.DisposeAsync(); }
            catch (JSDisconnectedException) { }
        }
    }
}
