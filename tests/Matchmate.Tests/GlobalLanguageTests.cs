using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Resources;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Matchmate.Infrastructure;
using Matchmate.Web.Components.Experience;
using Matchmate.Web.Components.Layout;
using Matchmate.Web.Components.Pages;
using Matchmate.Web.Experience;
using Matchmate.Web.Localization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Xunit;

namespace Matchmate.Tests;

public sealed class GlobalLanguageTests
{
    [Fact]
    public void ShellResourcesCoverEveryLanguageWithExplicitCultureAndFallback()
    {
        Dictionary<string, string> Read(string suffix) => XDocument.Load(Path.Combine(LocalPaths.FindRoot(), "src", "Matchmate.Web", "Localization", "ShellText" + suffix + ".resx"))
            .Root!.Elements("data").ToDictionary(element => element.Attribute("name")!.Value, element => element.Element("value")!.Value);
        var english = Read("");
        Assert.True(english.Count >= 40);
        var culture = CultureInfo.CurrentCulture; var uiCulture = CultureInfo.CurrentUICulture;
        foreach (var (language, suffix) in new[] { (MatchLanguage.English, ""), (MatchLanguage.Swedish, ".sv"), (MatchLanguage.Spanish, ".es") })
        {
            var resources = Read(suffix);
            Assert.Equal(english.Keys.Order(), resources.Keys.Order());
            foreach (var (key, value) in resources)
            {
                Assert.False(string.IsNullOrWhiteSpace(value));
                Assert.Equal(value, ShellText.Get(language, key));
            }
            Assert.Equal(8, ShellText.Recovery(language).Count);
        }
        Assert.Same(culture, CultureInfo.CurrentCulture); Assert.Same(uiCulture, CultureInfo.CurrentUICulture);
        Assert.Equal("Language", ShellText.Get((MatchLanguage)99, "LanguageLabel"));
        Assert.Throws<MissingManifestResourceException>(() => ShellText.Get(MatchLanguage.Spanish, "Missing.Resource"));
    }

    [Theory]
    [InlineData("en-GB", MatchLanguage.English)]
    [InlineData("sv-SE", MatchLanguage.Swedish)]
    [InlineData("es-ES", MatchLanguage.Spanish)]
    [InlineData("fr-FR", MatchLanguage.English)]
    public void RequestSeedAndOneBrowserReconciliationStayWithinEachCircuit(string cultureName, MatchLanguage expected)
    {
        var culture = CultureInfo.CurrentCulture; var uiCulture = CultureInfo.CurrentUICulture;
        var state = new MatchLanguageState(MatchLanguages.FromCulture(CultureInfo.GetCultureInfo(cultureName)));
        var other = new MatchLanguageState(); var changes = 0;
        state.Changed += () => changes++;
        Assert.Equal(expected, state.Language);
        state.ReconcileBrowserPreference(MatchLanguage.Spanish);
        Assert.True(state.BrowserPreferenceReconciled); Assert.Equal(MatchLanguage.Spanish, state.Language);
        Assert.False(state.ReconcileBrowserPreference(MatchLanguage.English));
        Assert.Equal(expected == MatchLanguage.Spanish ? 0 : 1, changes);
        Assert.Equal(MatchLanguage.English, other.Language); Assert.False(other.BrowserPreferenceReconciled);
        Assert.True(state.SetLanguage(MatchLanguage.Swedish)); Assert.False(state.SetLanguage((MatchLanguage)99));
        Assert.Equal(MatchLanguage.Swedish, state.Language);
        Assert.Same(culture, CultureInfo.CurrentCulture); Assert.Same(uiCulture, CultureInfo.CurrentUICulture);
    }

    [Theory]
    [InlineData("/", false)]
    [InlineData("/recordings?stream=a&cursor=61.2", false)]
    [InlineData("/recordings/one#pitch", false)]
    [InlineData("/league/2026", false)]
    [InlineData("/match/one", false)]
    [InlineData("/account/sign-in?returnUrl=%2Frecordings%3Fcursor%3D61.2", true)]
    [InlineData("/account/denied", true)]
    [InlineData("/account/error", true)]
    [InlineData("/Error", true)]
    [InlineData("/not-found", true)]
    public void OnlyServerRenderedUtilityRoutesNeedRefresh(string path, bool expected) =>
        Assert.Equal(expected, ShellText.RequiresPageRefresh("https://unit.invalid" + path));

    [Theory]
    [InlineData(MatchLanguage.English)]
    [InlineData(MatchLanguage.Swedish)]
    [InlineData(MatchLanguage.Spanish)]
    public async Task SelectorAndFooterUseTheSharedStateDuringStaticRenderingWithoutInterop(MatchLanguage language)
    {
        await using var services = Services(language); var state = services.GetRequiredService<MatchLanguageState>();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var selector = await renderer.RenderComponentAsync<ReplayLanguageSelector>();
            var footer = await renderer.RenderComponentAsync<ShellFooter>();
            var html = WebUtility.HtmlDecode(selector.ToHtmlString());
            Assert.DoesNotContain("data-standalone-language", html);
            Assert.DoesNotContain("data-language-save-failed", html);
            Assert.DoesNotContain("data-culture", html);
            Assert.Contains("for=\"matchmate-language\"", html); Assert.Contains("id=\"matchmate-language\"", html);
            Assert.Contains(ShellText.Get(language, "LanguageLabel"), html);
            foreach (var name in new[] { "English", "Svenska", "Español" }) Assert.Contains(name, html);
            Assert.DoesNotContain("LanguageScope", html); Assert.DoesNotContain("replay-language-scope", html);
            Assert.Contains(ShellText.Get(language, "Footer"), WebUtility.HtmlDecode(footer.ToHtmlString()));
            state.SetLanguage(language == MatchLanguage.Spanish ? MatchLanguage.Swedish : MatchLanguage.Spanish);
            Assert.Contains(ShellText.Get(state.Language, "LanguageLabel"), WebUtility.HtmlDecode(selector.ToHtmlString()));
            Assert.Contains(ShellText.Get(state.Language, "Footer"), WebUtility.HtmlDecode(footer.ToHtmlString()));
        });
    }

    [Theory]
    [InlineData(MatchLanguage.English)]
    [InlineData(MatchLanguage.Swedish)]
    [InlineData(MatchLanguage.Spanish)]
    public async Task StandaloneSelectorRendersTheSelectedCultureAndLocalizedRetryWithoutInterop(MatchLanguage language)
    {
        await using var services = Services(language);
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var selector = await renderer.RenderComponentAsync<ReplayLanguageSelector>(ParameterView.FromDictionary(new Dictionary<string, object?>
            { [nameof(ReplayLanguageSelector.Standalone)] = true }));
            var html = WebUtility.HtmlDecode(selector.ToHtmlString());
            Assert.Contains("data-standalone-language=\"\"", html);
            Assert.Contains("data-language-save-failed=\"" + ShellText.Get(language, "LanguageSaveFailed") + "\"", html);
            Assert.Matches("<select[^>]*\\bdisabled(?:[ =>])", html);
            Assert.Matches("<span[^>]*role=\"status\"[^>]*data-language-error[^>]*hidden", html);
            var selected = Assert.Single(Regex.Matches(html, "<option\\b[^>]*>").Cast<Match>(), option => Regex.IsMatch(option.Value, "\\bselected(?:[ =>])"));
            Assert.Contains("value=\"" + language + "\"", selected.Value);
            Assert.Contains("data-culture=\"" + MatchLanguages.Culture(language).Name + "\"", selected.Value);
            foreach (var supported in MatchLanguages.Supported)
                Assert.Contains("data-culture=\"" + MatchLanguages.Culture(supported).Name + "\"", html);
        });
    }

    [Theory]
    [InlineData(MatchLanguage.English)]
    [InlineData(MatchLanguage.Swedish)]
    [InlineData(MatchLanguage.Spanish)]
    public async Task StaticAccountAndErrorPagesTranslateWithoutChangingLoginReturnUrl(MatchLanguage language)
    {
        await using var services = Services(language);
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            const string returnUrl = "/recordings?stream=one&cursor=61.2";
            var signIn = await renderer.RenderComponentAsync<SignIn>();
            var html = WebUtility.HtmlDecode(signIn.ToHtmlString());
            Assert.Contains(ShellText.Get(language, "SignInDescription"), html);
            Assert.Contains("/auth/login?returnUrl=%2F", html);
            var signInWithReturn = new SignIn();
            typeof(SignIn).GetProperty(nameof(SignIn.ReturnUrl))!.SetValue(signInWithReturn, returnUrl);
            Assert.Equal("/auth/login?returnUrl=" + Uri.EscapeDataString(returnUrl), typeof(SignIn).GetProperty("LoginUrl", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(signInWithReturn));
            foreach (var (component, heading) in new[] { (typeof(AccessDenied), "AccessHeading"), (typeof(AuthenticationError), "AuthenticationErrorHeading"), (typeof(Error), "ErrorHeading"), (typeof(NotFound), "NotFoundHeading") })
            {
                var rendered = await renderer.RenderComponentAsync(component, ParameterView.Empty);
                Assert.Contains(ShellText.Get(language, heading), WebUtility.HtmlDecode(rendered.ToHtmlString()));
            }
            using var request = new Activity("error-page-test").Start();
            var error = await renderer.RenderComponentAsync<Error>();
            var errorHtml = WebUtility.HtmlDecode(error.ToHtmlString());
            Assert.Contains("<main class=\"experience error-page\"", errorHtml);
            Assert.Contains("href=\"/account/sign-in\" data-enhance-nav=\"false\"", errorHtml);
            foreach (var key in new[] { "ErrorBeforeLink", "SignInLink", "ErrorAfterLink", "RequestId" })
                Assert.Contains(ShellText.Get(language, key), errorHtml);
            Assert.Contains("<code", errorHtml); Assert.Contains(request.Id!, errorHtml);
        });
    }

    [Fact]
    public async Task TypedInteropImportsOnceBatchesPreferenceAndRecoveryTextAndDisposesOnce()
    {
        var js = new RecordingJs(); var interop = new LanguagePreferenceInterop(js);
        Assert.Equal(MatchLanguage.Swedish, await interop.InitializeAsync(MatchLanguage.English));
        var saved = await interop.SaveAsync(MatchLanguage.Spanish);
        Assert.True(saved.Saved); Assert.False(saved.RefreshNotFound);
        await interop.UpdateDocumentAsync(MatchLanguage.Spanish);
        Assert.Single(js.Imports); Assert.Equal("import", js.Imports[0].Name);
        Assert.Equal("./Components/Experience/ReplayLanguageSelector.razor.js", Assert.Single(js.Imports[0].Args!));
        Assert.Equal(new[] { "initializeLanguage", "saveLanguage", "updateDocumentLanguage" }, js.Module.Calls.Select(call => call.Name));
        Assert.Equal("en-GB", js.Module.Calls[0].Args![0]);
        Assert.Equal("es-ES", js.Module.Calls[1].Args![0]);
        Assert.Equal(ShellText.Recovery(MatchLanguage.Spanish), Assert.IsType<Dictionary<string, string>>(js.Module.Calls[1].Args![1]));
        Assert.False((await interop.SaveAsync((MatchLanguage)99)).Saved); Assert.Equal(3, js.Module.Calls.Count);
        await interop.DisposeAsync(); await interop.DisposeAsync();
        Assert.Equal(1, js.Module.Disposals);
        Assert.False((await interop.SaveAsync(MatchLanguage.Swedish)).Saved); Assert.Equal(3, js.Module.Calls.Count);
    }

    [Fact]
    public async Task DisconnectedModuleCanBeDisposedWithoutASecondImport()
    {
        var js = new RecordingJs(); var interop = new LanguagePreferenceInterop(js);
        await interop.InitializeAsync(MatchLanguage.English); js.Module.DisconnectOnDispose = true;
        await interop.DisposeAsync(); Assert.Single(js.Imports); Assert.Equal(1, js.Module.Disposals);
    }

    private static ServiceProvider Services(MatchLanguage language) => new ServiceCollection().AddLogging()
        .AddSingleton(new MatchLanguageState(language)).AddSingleton<NavigationManager>(new UnitNavigation())
        .AddSingleton<IJSRuntime>(new NoStaticJs()).BuildServiceProvider();
    private sealed class UnitNavigation : NavigationManager
    {
        public UnitNavigation() => Initialize("https://unit.invalid/", "https://unit.invalid/account/sign-in");
        protected override void NavigateToCore(string uri, bool forceLoad) => throw new InvalidOperationException("Rendering must not navigate.");
    }
    private sealed class NoStaticJs : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => throw new InvalidOperationException("Static rendering must not invoke JS.");
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken token, object?[]? args) => throw new InvalidOperationException("Static rendering must not invoke JS.");
    }
    private sealed class RecordingJs : IJSRuntime
    {
        public List<(string Name, object?[]? Args)> Imports { get; } = [];
        public RecordingModule Module { get; } = new();
        private ValueTask<TValue> Record<TValue>(string identifier, object?[]? args)
        {
            Imports.Add((identifier, args)); return ValueTask.FromResult((TValue)(object)Module);
        }
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            try { return Record<TValue>(identifier, args); }
            catch (JSException) { throw; }
        }
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken token, object?[]? args)
        {
            try { return Record<TValue>(identifier, args); }
            catch (JSException) { throw; }
        }
    }
    private sealed class RecordingModule : IJSObjectReference
    {
        public List<(string Name, object?[]? Args)> Calls { get; } = [];
        public int Disposals { get; private set; }
        public bool DisconnectOnDispose { get; set; }
        private ValueTask<TValue> Record<TValue>(string identifier, object?[]? args)
        {
            Calls.Add((identifier, args));
            object? result = identifier switch { "initializeLanguage" => "sv-SE", "saveLanguage" => new LanguagePreferenceSave(true, false), _ => default(TValue) };
            return ValueTask.FromResult((TValue)result!);
        }
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            try { return Record<TValue>(identifier, args); }
            catch (JSException) { throw; }
        }
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken token, object?[]? args)
        {
            try { return Record<TValue>(identifier, args); }
            catch (JSException) { throw; }
        }
        public ValueTask DisposeAsync()
        {
            Disposals++; if (DisconnectOnDispose) throw new JSDisconnectedException("test disconnect"); return ValueTask.CompletedTask;
        }
    }
}
