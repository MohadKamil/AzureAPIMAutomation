using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Interfaces;
using Microsoft.OpenApi.Models;
using Microsoft.OpenApi.Readers;
using Microsoft.OpenApi.Writers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Pulumi;
using Pulumi.AzureNative.ApiManagement;
using Pulumi.AzureNative.ApiManagement.Inputs;
using ApiSchema = Pulumi.AzureNative.ApiManagement.V20210801.ApiSchema;


namespace AzureAPIMAutomation
{
    public class AzureApiManagementApiComponent : ComponentResource
    {
        private static readonly HashSet<string> FormSchemaNames = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase)
        {
            "multipart/form-data",
            "application/x-www-form-urlencoded"
        };

        private const string ApiSchemaId = "apischema";
        private const string PolicyKey = "x-az-apim-inbound-append-policy";

        private const string PolicySchema =
            "<policies>\n      <inbound>\n          <base />\n          {0}      </inbound>\n      <backend>\n          <base />\n      </backend>\n      <outbound>\n          <base />\n      </outbound>\n      <on-error>\n          <base />\n      </on-error>\n  </policies>";

        public AzureApiManagementApiComponent(string componentName, ApiConfigurations apiConfigurations, ComponentResourceOptions options = null) : base(
            "AhoyIAC:Components:APIComponent", componentName, options)
        {
            var apiManagementName = apiConfigurations.APIManagementName;
            var apiResourceGroup = apiConfigurations.APIManagementResourceGroup;
            var documentUrl = apiConfigurations.OpenAPIDocumentUrl;
            var apiPath = apiConfigurations.APIPath;
            var apiServiceUrl = apiConfigurations.APIServiceUrl;

            var openApiDocument = GetApiDocument(documentUrl);
            
            var api = new Api(openApiDocument.Info.Title, new ApiArgs
            {
                ApiId = openApiDocument.Info.Title.ToLowerInvariant().Trim().Replace(" ", string.Empty),
                ResourceGroupName = apiResourceGroup,
                ServiceName = apiConfigurations.APIManagementName,
                Path = apiPath,
                ServiceUrl = apiServiceUrl,
                Description = openApiDocument.Info.Description,
                Protocols= new InputList<Protocol>
                {
                    Protocol.Https
                },
                DisplayName = openApiDocument.Info.Title
            });
            openApiDocument.Extensions.TryGetValue(PolicyKey, out var apiPolicyExtension);
            if(apiPolicyExtension is OpenApiString apiPolicyExtensionString)
            {
                var fullPolicy = string.Format(PolicySchema, apiPolicyExtensionString.Value);
                var apiPolicy = new ApiPolicy("api-policy", new ApiPolicyArgs
                {
                    ApiId = api.Name,
                    PolicyId = "policy",
                    ResourceGroupName = apiResourceGroup,
                    Value = fullPolicy,
                    Format = PolicyContentFormat.Xml,
                    ServiceName = apiConfigurations.APIManagementName
                });
            }
            var jsonSchemas = openApiDocument.Components.Schemas
                .ToDictionary(p => p.Key,
                    p =>
                    {
                        var (_, schema) = p;
                        return JsonExtensions.JsonElementFromObject(new {type = schema.Type});
                    });
            var componentSchema = JsonExtensions.JsonElementFromObject(new {schemas = jsonSchemas});
            var apiSchema = new Pulumi.AzureNative.ApiManagement.V20210801.ApiSchema("schema",
                new Pulumi.AzureNative.ApiManagement.V20210801.ApiSchemaArgs
                {
                    ResourceGroupName = apiResourceGroup,
                    ServiceName = apiManagementName,
                    ApiId = api.Name,
                    ContentType = "application/vnd.oai.openapi.components+json",
                    SchemaId = ApiSchemaId,
                    Components = componentSchema
                });
            
            foreach (var (pathTemplate, openApiPathItem) in openApiDocument.Paths)
            {
                foreach (var (operationType, openApiOperation) in openApiPathItem.Operations)
                {
                    var operationId = string.IsNullOrWhiteSpace(openApiOperation.OperationId)
                        ? Guid.NewGuid().ToString()
                        : openApiOperation.OperationId;
                    var displayName = string.IsNullOrWhiteSpace(openApiOperation.Summary)
                        ? pathTemplate
                        : openApiOperation.Summary;
                    
                    var allRouteParameters = openApiOperation.Parameters.Concat(openApiPathItem.Parameters);
                    var apiOperationResponses = openApiOperation.Responses?
                        .Where(r => int.TryParse(r.Key, out var _))
                        .Select(r =>
                        {
                            var (statusCode, openApiResponseValue) = r;
                            int.TryParse(statusCode, out var responseStatusCode);
                            
                            return new ResponseContractArgs
                            {
                                Description = openApiResponseValue.Description,
                                StatusCode = responseStatusCode,
                                Representations = openApiResponseValue.Content?.Select(c =>
                                {
                                    var (contentType, openApiMediaType) = c;
                                    var candidateTypeName = openApiMediaType.Schema?.Reference?.Id ?? string.Empty;
                                    var schemaNameAndType = GetSchemaIdOnlyIfNonFormData(contentType,apiSchema,candidateTypeName);
                                    return new RepresentationContractArgs
                                    {
                                        Sample = GetExampleOfPropertiesExample(openApiMediaType),
                                        SchemaId = schemaNameAndType.Apply(s => s.schemaName),
                                        TypeName = schemaNameAndType.Apply(s => s.typeName),
                                        ContentType = contentType
                                    };
                                }).ToList() ?? new InputList<RepresentationContractArgs>(),
                                Headers = openApiResponseValue.Headers.Select(h =>
                                {
                                    var (headerName, openApiHeader) = h;
                                    return new ParameterContractArgs
                                    {
                                        Type = openApiHeader.Schema.Type,
                                        Description = openApiHeader.Description,
                                        Name = headerName,
                                        Required = openApiHeader.Required,
                                        DefaultValue = openApiHeader.Schema.Default?.ToString() ?? string.Empty,
                                    };
                                }).ToList()
                            };
                        }).ToList() ?? new List<ResponseContractArgs>();
                    var operation = new ApiOperation(operationId, new ApiOperationArgs
                    {
                        ResourceGroupName = apiResourceGroup,
                        ServiceName = apiConfigurations.APIManagementName,
                        DisplayName = displayName,
                        ApiId = api.Name,
                        Method = operationType.ToString(),
                        UrlTemplate = pathTemplate,
                        TemplateParameters = allRouteParameters?.Where(p => p.In == ParameterLocation.Path)
                            .Select(p => new ParameterContractArgs
                            {
                                Name = p.Name,
                                DefaultValue = p.Schema.Default?.ToString() ?? string.Empty,
                                Description = p.Description,
                                Required = p.Required,
                                Type = p.Schema.Type
                            }).ToList() ?? new InputList<ParameterContractArgs>(),
                        Description = openApiOperation.Description,
                        OperationId = openApiOperation.OperationId,
                        Responses = apiOperationResponses,
                        Request = new RequestContractArgs
                        {
                            Representations = openApiOperation.RequestBody?.Content?
                                .Where(c => !string.IsNullOrWhiteSpace(c.Value?.Schema?.Reference?.Id))
                                .Select(c =>
                            {
                                var (contentType, openApiMediaType) = c;
                                var candidateTypeName = openApiMediaType.Schema?.Reference?.Id ?? string.Empty;
                                var schemaNameAndType = GetSchemaIdOnlyIfNonFormData(contentType,apiSchema,candidateTypeName);
                                return new RepresentationContractArgs
                                {
                                    ContentType = contentType,
                                    Sample = GetExampleOfPropertiesExample(openApiMediaType.Schema),
                                    SchemaId = schemaNameAndType.Apply(s => s.schemaName),
                                    TypeName = schemaNameAndType.Apply(s => s.typeName)
                                };
                            }).ToList() ?? new List<RepresentationContractArgs>(),
                            Description = openApiOperation.Description,
                            Headers = openApiOperation.Parameters?.Where(p => p.In == ParameterLocation.Header).Select(
                                p => new ParameterContractArgs
                                {
                                    Name = p.Name,
                                    Required = p.Required,
                                    Type = p.Schema?.Type ?? string.Empty,
                                    Description = p.Description,
                                    DefaultValue = p.Schema?.Default?.ToString() ?? string.Empty
                                }).ToList() ?? new List<ParameterContractArgs>(),
                            QueryParameters = openApiOperation.Parameters?.Where(p => p.In == ParameterLocation.Query)
                                .Select(p => new ParameterContractArgs
                                {
                                    Type = p.Schema.Type,
                                    Description = p.Description,
                                    DefaultValue = p.Schema.Default?.ToString() ?? string.Empty,
                                    Name = p.Name,
                                    Required = p.Required
                                }).ToList() ?? new List<ParameterContractArgs>()
                        }
                    },
                         new CustomResourceOptions
                         {
                             DependsOn = new InputList<Resource>
                             {
                                 apiSchema
                             }
                         });

                    openApiOperation.Extensions.TryGetValue(PolicyKey, out var operationPolicyExtension);
                    if (operationPolicyExtension is OpenApiString operationPolicyExtensionString)
                    {
                        var fullPolicy = string.Format(PolicySchema, operationPolicyExtensionString.Value);
                        var operationPolicy = new ApiOperationPolicy($"operation-{operationId}-policy", new ApiOperationPolicyArgs
                        {
                            ApiId = api.Name,
                            OperationId = operation.Name,
                            PolicyId = "policy",
                            ResourceGroupName = apiResourceGroup,
                            Value = fullPolicy,
                            Format = PolicyContentFormat.Xml,
                            ServiceName = apiConfigurations.APIManagementName
                        });
                    }
                }
            }

            RegisterOutputs();
        }

        private static Output<(string? schemaName, string? typeName)> GetSchemaIdOnlyIfNonFormData(string contentType,
            ApiSchema schema,
            string candidateTypeName)
        {
            return schema.Name.Apply(n => FormSchemaNames.Contains(contentType) ? (null,null) : (n,candidateTypeName));
        }


        private static OpenApiDocument GetApiDocument(string documentUri)
        {
            Stream stream;
            if (documentUri.ToLower().StartsWith("http"))
            {
                var httpClient = new HttpClient();

                stream = httpClient.GetStreamAsync(documentUri).Result;
            }
            else
            {
                stream = File.OpenRead(documentUri);
            }

            var openApiDocument = new OpenApiStreamReader().Read(stream, out _);

            return openApiDocument;

        }
        private static string ExtractJson(IOpenApiExtension? openApiAny)
        {
            if (openApiAny == null)
            {
                return string.Empty;
            }
            var stringBuilder = new StringBuilder();
            openApiAny.Write(new OpenApiJsonWriter(new StringWriter(stringBuilder)), OpenApiSpecVersion.OpenApi3_0);
            var jsonExample = stringBuilder.ToString();
            var jObject = JsonConvert.DeserializeObject<JToken>(jsonExample);
            return JsonConvert.SerializeObject(jObject, Formatting.None);
        }
        
        private static string ExtractJson(IOpenApiReferenceable openApiAny)
        {
            var stringBuilder = new StringBuilder();
            openApiAny.SerializeAsV3WithoutReference(new OpenApiJsonWriter(new StringWriter(stringBuilder)));
            var jsonExample = stringBuilder.ToString();
            var jObject = JsonConvert.DeserializeObject<JToken>(jsonExample);
            return JsonConvert.SerializeObject(jObject, Formatting.None);
        }
        
        private static string GetExampleOfPropertiesExample(OpenApiMediaType openApiMediaType)
        {
            return openApiMediaType.Example != null 
                ? ExtractJson(openApiMediaType.Example) 
                : GetExampleOfPropertiesExample(openApiMediaType.Schema);
        }
        
        private static string GetExampleOfPropertiesExample(OpenApiSchema schema)
        {
            if(schema.Example != null)
            {
                return ExtractJson(schema.Example);
            }
	
            JToken jobject = new JObject();
            if(schema.Items != null)
            {
                var jarray = new JArray();
                var exampleObject =  new JObject();
                foreach (var prop in schema.Items.Properties)
                {
                    if (prop.Value.Example != null)
                    {
                        exampleObject[prop.Key] = ExtractJson(prop.Value.Example);
                    }
                    else
                    {
                        exampleObject[prop.Key] = GetDefaultExample(prop.Value.Type);
                    }
                }
                jarray.Add(exampleObject);
                jobject = jarray;
            }
            else
            {
                foreach (var (key, value) in schema.Properties)
                {
                    if (value.Example != null)
                    {
                        jobject[key] = ExtractJson(value.Example);
                    }
                }
            }
            
            return JsonConvert.SerializeObject(jobject, Formatting.None);
            
            JToken GetDefaultExample(string type)
            {
                return type.ToLower() switch
                {
                    "string" => new JValue("string"),
                    "integer" => new JValue(0),
                    "boolean" => new JValue(false),
                    "number" => new JValue(0.0),
                    _ => new JValue("")
                };
            }
        }
        
        
    }
}