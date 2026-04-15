using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace Jellyfin.Plugin.StreamGenerator;

public class MasterHlsInterceptionFilter(ILogger<MasterHlsInterceptionFilter> logger) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (context.RouteData.Values.TryGetValue("controller", out var controller) && controller is "DynamicHls"
            && context.RouteData.Values.TryGetValue("action", out var action) && action is "GetMasterHlsVideoPlaylist")
        {
            if (context.ActionArguments.TryGetValue("playSessionId", out var playSessionIdObj)
                && playSessionIdObj?.ToString() is "stream_generator_random")
            {
                var newValue = "stream_generator_" + Random.Shared.Next(1000000000);
                logger.LogDebug("Replacing playSessionId with {NewValue}", newValue);
                context.ActionArguments["playSessionId"] = newValue;

                // Not sure about that
                if (context.ActionArguments.TryGetValue("streamOptions", out var optionsObj) 
                    && optionsObj is Dictionary<string, string> streamOptions)
                {
                    if (streamOptions.ContainsKey("playSessionId"))
                    {
                        streamOptions["playSessionId"] = newValue;
                    }
                }

                var request = context.HttpContext.Request;
                var queryDict = QueryHelpers.ParseQuery(request.QueryString.Value);
                queryDict["playSessionId"] = new StringValues(newValue);
                request.QueryString = QueryString.Create(queryDict);
            }
            
        }

        await next();
    }
}