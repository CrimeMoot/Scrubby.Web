using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Scrubby.Web.AssetIndex;
using Scrubby.Web.Services;
using Scrubby.Web.Services.Interfaces;

namespace Scrubby.Web;

public class Startup(IConfiguration configuration)
{
    public IConfiguration Configuration { get; } = configuration;

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IAppStatusService, AppStatusService>();

        services.AddMvc()
            .AddRazorRuntimeCompilation()
            .AddNewtonsoftJson(options =>
            {
                options.SerializerSettings.NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore;
            });

        services.AddWebOptimizer(pipeline =>
        {
            pipeline.AddCssBundle("/css/bundle.css",
                "/lib/bootstrap/css/bootstrap.css",
                "/lib/bootstrap-toggle/css/bootstrap4-toggle.css",
                "/lib/font-awesome/css/all.css",
                "/lib/datatables/datatables.css",
                "/lib/bootstrap-select/css/bootstrap-select.css",
                "/lib/tagify/tagify.css",
                "/lib/prism/prism.css",
                "/css/site.css",
                "/css/file.css");

            pipeline.AddJavaScriptBundle("/js/bundle.js",
                "/lib/jquery/dist/jquery.js",
                "/lib/moment/js/moment.js",
                "/lib/moment/js/moment-duration-format.js",
                "/lib/infinite-scroll/infinite-scroll.pkgd.min.js",
                "/lib/hammer/hammer.min.js",
                "/lib/d3/d3.js",
                "/lib/bootstrap/js/bootstrap.bundle.js",
                "/lib/bootstrap-toggle/js/bootstrap4-toggle.js",
                "/lib/tagify/tagify.js",
                "/lib/clipboard/clipboard.js",
                "/lib/datatables/datatables.js",
                "/lib/jquery-scrollTo/js/jquery.scrollTo.js",
                "/lib/bootstrap-select/js/bootstrap-select.js",
                "/lib/prism/prism.js",
                "/js/site.js");
        });

        services.AddSingleton<AssetIndexService>();
        services.AddHostedService<AssetIndexHostedService>();
    }

    public void Configure(IApplicationBuilder app, IWebHostEnvironment env, ILogger<Startup> logger)
    {
        if (env.IsDevelopment())
            app.UseDeveloperExceptionPage();
        else
        {
            app.UseExceptionHandler(new ExceptionHandlerOptions
            {
                AllowStatusCode404Response = true,
                ExceptionHandlingPath = "/exception"
            });
            app.UseHsts();
        }

        app.UseStatusCodePagesWithReExecute("/error", "?statusCode={0}");
        app.UseWebOptimizer();
        app.UseStaticFiles();
        app.UseRouting();

        app.UseEndpoints(endpoints =>
        {
            endpoints.MapControllerRoute("default", "{controller=Home}/{action=Index}/{id?}");
        });
    }
}
