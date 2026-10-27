using Matchmate.Web.Localization;
using Microsoft.JSInterop;

namespace Matchmate.Web.Experience;

public sealed class LanguagePreferenceInterop(IJSRuntime js) : IAsyncDisposable
{
    internal const string ModulePath = "./Components/Experience/ReplayLanguageSelector.razor.js";
    internal const string ImportMethod = "import";
    internal const string InitializeMethod = "initializeLanguage";
    internal const string SaveMethod = "saveLanguage";
    internal const string UpdateMethod = "updateDocumentLanguage";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IJSObjectReference? _module;
    private int _disposed;

    public async ValueTask<MatchLanguage> InitializeAsync(MatchLanguage fallback)
    {
        await _gate.WaitAsync();
        try
        {
            var module = await GetModuleAsync();
            if (module is null) return fallback;
            var recovery = MatchLanguages.Supported.ToDictionary(language => MatchLanguages.Culture(language).Name, ShellText.Recovery);
            var culture = await module.InvokeAsync<string>(InitializeMethod, MatchLanguages.Culture(fallback).Name, recovery);
            return culture switch { "sv-SE" => MatchLanguage.Swedish, "es-ES" => MatchLanguage.Spanish, _ => MatchLanguage.English };
        }
        // The component owns the localized error state; preserve circuit loss and JS failures for it.
        catch (JSDisconnectedException) { throw; }
        catch (JSException) { throw; }
        finally { _gate.Release(); }
    }
    public async ValueTask<LanguagePreferenceSave> SaveAsync(MatchLanguage language)
    {
        if (!Enum.IsDefined(language)) return new(false, false);
        await _gate.WaitAsync();
        try
        {
            var module = await GetModuleAsync();
            return module is null ? new(false, false) : await module.InvokeAsync<LanguagePreferenceSave>(SaveMethod, MatchLanguages.Culture(language).Name, ShellText.Recovery(language));
        }
        catch (JSDisconnectedException) { throw; }
        catch (JSException) { throw; }
        finally { _gate.Release(); }
    }
    public async ValueTask UpdateDocumentAsync(MatchLanguage language)
    {
        await _gate.WaitAsync();
        try
        {
            var module = await GetModuleAsync();
            if (module is not null) await module.InvokeVoidAsync(UpdateMethod, MatchLanguages.Culture(language).Name, ShellText.Recovery(language));
        }
        catch (JSDisconnectedException) { throw; }
        catch (JSException) { throw; }
        finally { _gate.Release(); }
    }
    private async ValueTask<IJSObjectReference?> GetModuleAsync()
    {
        if (Volatile.Read(ref _disposed) != 0) return null;
        try
        {
            _module ??= await js.InvokeAsync<IJSObjectReference>(ImportMethod, ModulePath);
            if (Volatile.Read(ref _disposed) == 0) return _module;
            var lateModule = _module; _module = null;
            await lateModule.DisposeAsync();
            return null;
        }
        catch (JSDisconnectedException) { throw; }
        catch (JSException) { throw; }
    }
    public async ValueTask DisposeAsync()
    {
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

public sealed record LanguagePreferenceSave(bool Saved, bool RefreshNotFound);
