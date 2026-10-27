using DaprMQ.ApiServer.Filters;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;

namespace DaprMQ.Tests;

public class DisableFormValueBindingAttributeTests
{
    [Fact]
    public void OnResourceExecuting_RemovesFormValueProviders_KeepsTheRest()
    {
        var factories = new List<IValueProviderFactory>
        {
            new FormValueProviderFactory(),
            new FormFileValueProviderFactory(),
            new JQueryFormValueProviderFactory(),
            new RouteValueProviderFactory(),
            new QueryStringValueProviderFactory(),
        };
        var actionContext = new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor());
        var context = new ResourceExecutingContext(actionContext, new List<IFilterMetadata>(), factories);

        new DisableFormValueBindingAttribute().OnResourceExecuting(context);

        Assert.Collection(context.ValueProviderFactories,
            f => Assert.IsType<RouteValueProviderFactory>(f),
            f => Assert.IsType<QueryStringValueProviderFactory>(f));
    }
}
