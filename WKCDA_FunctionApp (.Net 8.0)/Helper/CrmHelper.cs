using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using System;

namespace WKCDA_FunctionApp__.Net_8._0_.Helper
{
    public static class CrmHelper
    {
        public static ServiceClient GetServiceClient(string accessToken)
        {
            Uri orgUri = new Uri(Environment.GetEnvironmentVariable("CRM_URL"));
            ServiceClient svc = new ServiceClient(
                       orgUri,
                       async (string instanceUri) => accessToken,
                       true,   // useUniqueInstance
                       null    // optional ILogger
                   );

            return svc;
        }

        public static IOrganizationService GetService(string accessToken)
        {
            return (IOrganizationService)GetServiceClient(accessToken);
        }
    }
}
