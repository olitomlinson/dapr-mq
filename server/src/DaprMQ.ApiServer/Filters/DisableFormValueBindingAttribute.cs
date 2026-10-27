using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace DaprMQ.ApiServer.Filters;

/// <summary>
/// Stops MVC parsing the body as a form for an action whose body is raw bytes, so a form
/// Content-Type (application/x-www-form-urlencoded, multipart/form-data) is just a recorded value.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class DisableFormValueBindingAttribute : Attribute, IResourceFilter
{
    public void OnResourceExecuting(ResourceExecutingContext context)
    {
        context.ValueProviderFactories.RemoveType<FormValueProviderFactory>();
        context.ValueProviderFactories.RemoveType<FormFileValueProviderFactory>();
        context.ValueProviderFactories.RemoveType<JQueryFormValueProviderFactory>();
    }

    public void OnResourceExecuted(ResourceExecutedContext context)
    {
    }
}
