using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.PowerPlatform.Dataverse.Client;
using System.Security.Authentication;
using WKCDA_FunctionApp__.Net_8._0_.Helper;

namespace WKCDA_FunctionApp__.Net_8._0_.Model
{
    public abstract class WS_Base
    {
        protected readonly ILogger<WS_Base> _logger;

        public WS_Base(ILogger<WS_Base> logger)
        {
            _logger = logger;
        }

        public string GetBearerToken(HttpRequest req)
        {
            var authorizationHeader = req.Headers.Authorization.FirstOrDefault();

            if (!string.IsNullOrEmpty(authorizationHeader) && authorizationHeader.StartsWith("Bearer "))
            {
                return authorizationHeader.Substring("Bearer ".Length).Trim();
            }
            else
            {
                _logger.LogError($"Bearer Token is not found.");
                throw new AuthenticationException("authorization failed");
            }
        }

        public ServiceClient GetServiceClient(HttpRequest req)
        {
            var svc = CrmHelper.GetServiceClient(GetBearerToken(req));
            
            if (svc.IsReady)
            {
                return svc;
            }
            else
            {
                _logger.LogError($"svc.LastError:{svc.LastError}");
                throw new AuthenticationException("authorization failed");
            }
        }

        protected abstract Dictionary<string, string> GetMappings();
    }
}
