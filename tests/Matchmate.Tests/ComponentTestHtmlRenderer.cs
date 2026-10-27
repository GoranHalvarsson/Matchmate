using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.HtmlRendering.Infrastructure;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.Logging;

namespace Matchmate.Tests;

// Unit rendering of the actual component and its lifecycle, without a browser or app host.
// StaticHtmlRenderer suppresses DOM-dependent OnAfterRender work. Accepting the declared
// server mode here does not test circuits, authentication, JavaScript or browser behavior.
#pragma warning disable BL0006
internal sealed class ComponentTestHtmlRenderer(IServiceProvider services, ILoggerFactory logging) : StaticHtmlRenderer(services, logging)
{
    protected override IComponent ResolveComponentForRenderMode(Type componentType, int? parentComponentId, IComponentActivator componentActivator, IComponentRenderMode renderMode)
        => renderMode is InteractiveServerRenderMode ? componentActivator.CreateInstance(componentType)
            : throw new NotSupportedException($"Unit renderer does not support {renderMode.GetType().Name}.");

    public Task<HtmlRootComponent> RenderComponentAsync<T>() where T : IComponent => RenderComponentAsync<T>(ParameterView.Empty);

    public async Task<HtmlRootComponent> RenderComponentAsync<T>(ParameterView parameters) where T : IComponent
    {
        var root = BeginRenderingComponent(typeof(T), parameters);
        await root.QuiescenceTask;
        return root;
    }
}
#pragma warning restore BL0006
