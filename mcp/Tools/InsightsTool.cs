using System.ComponentModel;
using System.Net.Http.Headers;
using System.Text;
using Azure.Core;
using Azure.Identity;
using ModelContextProtocol.Server;
using Newtonsoft.Json;

/// <summary>
/// MCP tools for querying Azure Application Insights.
/// These tools can be invoked by MCP clients to perform Application Insights queries.
/// </summary>
internal class InsightsTool
{
    private static readonly HttpClient _httpClient = new HttpClient();
    private const string ApplicationId = "93d43214-c334-430e-aabb-61814f6a86b4";

    [McpServerTool]
    [Description(@"Executes a KQL query against Azure Application Insights and returns the results. All logs are saved as customEvents table.")]
    public async Task<string> QueryApplicationInsights([Description(@"The KQL query to execute")] string query)
    {
        try
        {
            var queryUrl = $"https://api.applicationinsights.io/v1/apps/{ApplicationId}/query";

            // Use DefaultAzureCredential with options to prioritize interactive credentials
            // Explicitly set tenant to ensure correct tenant is used for token acquisition
            var credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
            {
                TenantId = "1c97039d-13b2-4129-9f33-88b08238b012", // Microsoft Entra Tenant ID
                ExcludeManagedIdentityCredential = true,
                ExcludeWorkloadIdentityCredential = true,
                ExcludeAzurePowerShellCredential = false,
                ExcludeAzureCliCredential = false,
                ExcludeVisualStudioCredential = false,
                ExcludeVisualStudioCodeCredential = false,
                ExcludeInteractiveBrowserCredential = false

            });
            var tokenRequestContext = new TokenRequestContext(new[] { "https://api.applicationinsights.io/.default" });
            var tokenResult = await credential.GetTokenAsync(tokenRequestContext);

            var payload = new { query = query };
            var content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");

            var request = new HttpRequestMessage(HttpMethod.Post, queryUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenResult.Token);
            request.Content = content;

            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadAsStringAsync();
            return result;
        }
        catch (Exception ex)
        {
            return $"Error executing query: {ex.Message}\n\nStack Trace: {ex.StackTrace}\n\nInner Exception: {ex.InnerException?.Message}";
        }
    }
}