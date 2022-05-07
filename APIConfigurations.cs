using Pulumi;

public class ApiConfigurations
{
    public string APIName { get; set; } = string.Empty;
    public Input<string> APIManagementResourceGroup { get; set; } = string.Empty;
    public string OpenAPIDocumentUrl { get; set; } = string.Empty;
    public string APIPath { get; set; } = string.Empty;
    public Input<string> APIServiceUrl { get; set; } = string.Empty;
    public Input<string> APIManagementName { get; set; } = string.Empty;
}