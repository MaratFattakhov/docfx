// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using System.Web;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Microsoft.Docs.Build
{
    internal class OpsConfigAdapter : IDisposable
    {
        private const string MonikerDefinitionApi = "https://ops/monikerDefinition/";
        private const string MetadataSchemaApi = "https://ops/metadataschema/";
        private const string MarkdownValidationRulesApi = "https://ops/markdownvalidationrules/";

        private static readonly string s_opsToken = Environment.GetEnvironmentVariable("DOCS_OPS_TOKEN");
        private static readonly IReadOnlyDictionary<string, string> s_opsHeaders = new Dictionary<string, string>
        {
            { "X-OP-BuildUserToken", s_opsToken },
        };

        private static readonly string s_environment = Environment.GetEnvironmentVariable("DOCS_ENVIRONMENT");
        private static readonly bool s_isProduction = string.IsNullOrEmpty(s_environment) || string.Equals("PROD", s_environment, StringComparison.OrdinalIgnoreCase);

        private static readonly string s_buildServiceEndpoint = s_isProduction
            ? "https://op-build-prod.azurewebsites.net"
            : "https://op-build-sandbox2.azurewebsites.net";

        private static readonly string s_validationServiceEndpoint = s_isProduction
            ? "https://docs.microsoft.com/api/metadata"
            : "https://ppe.docs.microsoft.com/api/metadata";

        private readonly ErrorLog _errorLog;
        private readonly HttpClient _http = new HttpClient();
        private readonly (string, Func<Uri, Task<string>>)[] _apis;

        public OpsConfigAdapter(ErrorLog errorLog)
        {
            _errorLog = errorLog;
            _apis = new (string, Func<Uri, Task<string>>)[]
            {
                (MonikerDefinitionApi, GetMonikerDefinition),
                (MetadataSchemaApi, GetMetadataSchema),
                (MarkdownValidationRulesApi, GetMarkdownValidationRules),
            };
        }

        public async Task<JObject> GetBuildConfig(SourceInfo<string> name, string repository, string branch)
        {
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(repository))
            {
                return null;
            }

            var url = $"{s_buildServiceEndpoint}/v2/Queries/Docsets?git_repo_url={repository}&docset_query_status=Created";
            var docsetInfo = await Fetch(url, s_opsHeaders, on404: () => throw Errors.DocsetNotProvisioned(name).ToException(isError: false));
            var docsets = JsonConvert.DeserializeAnonymousType(
                docsetInfo,
                new[] { new { name = "", base_path = "", site_name = "", product_name = "" } });

            var docset = docsets.FirstOrDefault(d => string.Equals(d.name, name, StringComparison.OrdinalIgnoreCase));
            if (docset is null)
            {
                throw Errors.DocsetNotProvisioned(name).ToException(isError: false);
            }

            var metadataServiceQueryParams = $"?repository_url={HttpUtility.UrlEncode(repository)}&branch={HttpUtility.UrlEncode(branch)}";

            return new JObject
            {
                ["product"] = docset.product_name,
                ["siteName"] = docset.site_name,
                ["hostName"] = GetHostName(docset.site_name),
                ["basePath"] = docset.base_path,
                ["xrefHostName"] = GetXrefHostName(docset.site_name, branch),
                ["localization"] = new JObject
                {
                    ["defaultLocale"] = GetDefaultLocale(docset.site_name),
                },
                ["monikerDefinition"] = MonikerDefinitionApi,
                ["markdownValidationRules"] = $"{MarkdownValidationRulesApi}{metadataServiceQueryParams}",
                ["metadataSchema"] = new JArray(
                    Path.Combine(AppContext.BaseDirectory, "data/schemas/OpsMetadata.json"),
                    $"{MetadataSchemaApi}{metadataServiceQueryParams}"),
            };
        }

        public async Task<HttpResponseMessage> InterceptHttpRequest(HttpRequestMessage request)
        {
            foreach (var (baseUrl, rule) in _apis)
            {
                if (request.RequestUri.OriginalString.StartsWith(baseUrl))
                {
                    return new HttpResponseMessage { Content = new StringContent(await rule(request.RequestUri)) };
                }
            }
            return null;
        }

        public void Dispose()
        {
            _http.Dispose();
        }

        private Task<string> GetMonikerDefinition(Uri url)
        {
            return Fetch($"{s_buildServiceEndpoint}/v2/monikertrees/allfamiliesproductsmonikers", s_opsHeaders);
        }

        private async Task<string> GetMarkdownValidationRules(Uri url)
        {
            try
            {
                var headers = GetValidationServiceHeaders(url);

                return await Fetch($"{s_validationServiceEndpoint}/rules/content", headers);
            }
            catch (Exception ex)
            {
                Log.Write(ex);
                _errorLog.Write(Errors.ValidationIncomplete());
                return "{}";
            }
        }

        private async Task<string> GetMetadataSchema(Uri url)
        {
            try
            {
                var headers = GetValidationServiceHeaders(url);
                var rules = Fetch($"{s_validationServiceEndpoint}/rules", headers);
                var allowlists = Fetch($"{s_validationServiceEndpoint}/allowlists", headers);

                return OpsMetadataRuleConverter.GenerateJsonSchema(await rules, await allowlists);
            }
            catch (Exception ex)
            {
                Log.Write(ex);
                _errorLog.Write(Errors.ValidationIncomplete());
                return "{}";
            }
        }

        private static Dictionary<string, string> GetValidationServiceHeaders(Uri url)
        {
            var queries = HttpUtility.ParseQueryString(url.Query);

            return new Dictionary<string, string>()
            {
                { "X-Metadata-RepositoryUrl", queries["repository_url"] },
                { "X-Metadata-RepositoryBranch", queries["branch"] },
            };
        }

        private async Task<string> Fetch(string url, IReadOnlyDictionary<string, string> headers = null, Action on404 = null)
        {
            using (PerfScope.Start($"[{nameof(OpsConfigAdapter)}] Fetching '{url}'"))
            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            {
                if (headers != null)
                {
                    foreach (var (key, value) in headers)
                    {
                        request.Headers.TryAddWithoutValidation(key, value);
                    }
                }

                var response = await _http.SendAsync(request);
                if (response.Headers.TryGetValues("X-Metadata-Version", out var metadataVersion))
                {
                    _errorLog.Write(Errors.MetadataValidationRuleset(string.Join(',', metadataVersion)));
                }

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    on404?.Invoke();
                }
                return await response.EnsureSuccessStatusCode().Content.ReadAsStringAsync();
            }
        }

        private static string GetDefaultLocale(string siteName)
        {
            return siteName == "DocsAzureCN" ? "zh-cn" : "en-us";
        }

        private static string GetHostName(string siteName)
        {
            switch (siteName)
            {
                case "DocsAzureCN":
                    return s_isProduction ? "docs.azure.cn" : "ppe.docs.azure.cn";
                case "dev.microsoft.com":
                    return s_isProduction ? "developer.microsoft.com" : "devmsft-sandbox.azurewebsites.net";
                case "rd.microsoft.com":
                    return "rd.microsoft.com";
                default:
                    return s_isProduction ? "docs.microsoft.com" : "ppe.docs.microsoft.com";
            }
        }

        private static string GetXrefHostName(string siteName, string branch)
        {
            return !IsLive(branch) && s_isProduction ? $"review.{GetHostName(siteName)}" : GetHostName(siteName);
        }

        private static bool IsLive(string branch)
        {
            return branch == "live" || branch == "live-sxs";
        }
    }
}
