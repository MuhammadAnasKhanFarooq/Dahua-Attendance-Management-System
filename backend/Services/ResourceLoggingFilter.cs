using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Http;

namespace DahuaAttendanceAPI.Services
{
    public class ResourceLoggingFilter : IAsyncResourceFilter
    {
        public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
        {
            try
            {
                var req = context.HttpContext.Request;
                Console.WriteLine($"[MVC TRACE] ResourceFilter ENTRY METHOD={req.Method} PATH={req.Path} CONTENTTYPE={req.ContentType} CONTENTLENGTH={req.ContentLength} TIME={DateTime.UtcNow:o}");

                var result = await next();

                Console.WriteLine($"[MVC TRACE] ResourceFilter EXIT STATUS={context.HttpContext.Response.StatusCode} PATH={req.Path} TIME={DateTime.UtcNow:o}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MVC TRACE] ResourceFilter EXCEPTION: {ex.Message}");
                throw;
            }
        }
    }
}
