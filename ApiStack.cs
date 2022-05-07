using Pulumi;
using Pulumi.AzureNative.ApiManagement;
using Pulumi.AzureNative.ApiManagement.Inputs;
using Pulumi.AzureNative.Resources;

namespace AzureAPIMAutomation
{
    internal class ApiStack : Stack
    {
        private const string PetStoreOpenApiDocument =
            "https://github.com/OAI/OpenAPI-Specification/blob/main/examples/v3.0/petstore.yaml";
        public ApiStack()
        {
        
            var rg = new ResourceGroup("PetStoreRg", new ResourceGroupArgs
            {
                ResourceGroupName = "petStore-apim-rg"
            });

            var apim = new ApiManagementService("PetStoreAPIM", new ApiManagementServiceArgs
            {
                ResourceGroupName = rg.Name,
                PublisherEmail = "developer@mail.com",
                PublisherName = "Developer",
                Sku = new ApiManagementServiceSkuPropertiesArgs
                {
                    Name = "Consumption",
                    Capacity = 0
                }
            });
            var apiConfigurations = new ApiConfigurations
            {
                APIName = "Pet Store API",
                APIPath = "pet-store",
                APIManagementName = apim.Name,
                APIServiceUrl = string.Empty,
                APIManagementResourceGroup = rg.Name,
                OpenAPIDocumentUrl = PetStoreOpenApiDocument
            };
            var apiComponent = new AzureApiManagementApiComponent("PetStoreAPI", apiConfigurations);
        }

   
    }
}
