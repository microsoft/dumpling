using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using Newtonsoft.Json.Serialization;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;
using System.Net.Http.Headers;
using System.Web.Http;

namespace dumpling.web
{
    public static class WebApiConfig
    {
        public static CloudBlobClient BlobClient;

        public static void Register(HttpConfiguration config)
        {
            // Web API configuration and services

            // Web API routes
            config.MapHttpAttributeRoutes();

            config.Routes.MapHttpRoute(
                name: "DefaultApi",
                routeTemplate: "api/{controller}/{id}",
                defaults: new { id = RouteParameter.Optional }
            );
            
            WebApiConfig.BlobClient = CloudStorageAccount.Parse(ConfigurationManager.AppSettings["StorageConnectionString"]).CreateCloudBlobClient();

            var container = BlobClient.GetContainerReference(ConfigurationManager.AppSettings["ArtifactContainer"]);
            container.CreateIfNotExistsAsync().GetAwaiter().GetResult();
            
            // Added so that json get returned by normal browser call to REST gets.
            config.Formatters.JsonFormatter.SupportedMediaTypes.Add(new MediaTypeHeaderValue("text/html"));

            config.Formatters.JsonFormatter.SerializerSettings.ContractResolver = new CamelCasePropertyNamesContractResolver();

            config.EnsureInitialized();
        }
    }
}
