using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.Logging;
using System.Security.Authentication;
using WKCDA_FunctionApp__.Net_8._0_.Model;

namespace WKCDA_FunctionApp__.Net_8._0_.API
{
    public class HealthCheck : WS_Base
    {

        public HealthCheck(ILogger<HealthCheck> logger) : base(logger)
        {
        }

        [Function("HealthCheck")]
        public async Task<IActionResult> RunAsync([HttpTrigger(AuthorizationLevel.Function, "get")] HttpRequest req)
        {
            _logger.LogInformation("C# HTTP trigger function processed a request.");
            var service = GetServiceClient(req);

            if (service != null)
            {
                var response = (WhoAmIResponse)service.Execute(new WhoAmIRequest());

                return new OkObjectResult($"Connect target Env Successfully: {response.UserId}");
            }
            else
            {
                return new UnauthorizedResult();
            }


            
        }

        protected override Dictionary<string, string> GetMappings()
        {
            throw new NotImplementedException();
        }
    }
}
