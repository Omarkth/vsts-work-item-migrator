using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Common.Config;
using Logging;
using Microsoft.Azure.DevOps.Licensing.WebApi;
using Microsoft.Extensions.Logging;
using Microsoft.TeamFoundation.Framework.Common;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.Graph.Client;
using Microsoft.VisualStudio.Services.Identity;
using Microsoft.VisualStudio.Services.Identity.Client;
using Microsoft.VisualStudio.Services.Licensing;
using Microsoft.VisualStudio.Services.MemberEntitlementManagement.WebApi;
using static Microsoft.VisualStudio.Services.Graph.Constants;

namespace Common.Migration
{
    public class IdentityPreProcessor : IPhase1PreProcessor
    {
        static ILogger Logger { get; } = MigratorLogging.CreateLogger<IdentityPreProcessor>();
        private static string LicensedUsersGroup = SidIdentityHelper.ConstructWellKnownSid(0, 2048);
        private static SubjectDescriptor[] Groups = new[] { new SubjectDescriptor(SubjectType.VstsGroup, LicensedUsersGroup) };

        private IMigrationContext context;
        private GraphHttpClient graphClient;
        private MemberEntitlementManagementHttpClient entitlementHttpClient;
        private IdentityHttpClient identityHttpClient;

        public string Name => "Identity";

        public bool IsEnabled(ConfigJson config)
        {
            return config.EnsureIdentities;
        }

        public async Task Prepare(IMigrationContext context)
        {
            this.context = context;

            this.graphClient = context.TargetClient.Connection.GetClient<GraphHttpClient>();
            this.entitlementHttpClient = context.TargetClient.Connection.GetClient<MemberEntitlementManagementHttpClient>();
            this.identityHttpClient = context.TargetClient.Connection.GetClient<IdentityHttpClient>();
        }

        public async Task Process(IBatchMigrationContext batchContext)
        {
            HashSet<string> identitiesToProcess = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var sourceWorkItem in batchContext.SourceWorkItems)
            {
                foreach (var field in context.SourceIdentityFields)
                {
                    if (sourceWorkItem.Fields.TryGetValueIgnoringCase(field, out var identityObject) && MigrationHelpers.GetIdentityUniqueName(identityObject, out string identityValue))
                    {
                        if (!string.IsNullOrEmpty(identityValue) && identityValue.Contains("@"))
                        {
                            if (identityValue.Contains("<") && identityValue.Contains(">"))
                            {
                                // parse out email address from the combo string
                                identityValue = identityValue.Substring(identityValue.LastIndexOf("<") + 1, identityValue.LastIndexOf(">") - identityValue.LastIndexOf("<") - 1);
                            }

                            if (!identitiesToProcess.Contains(identityValue)
                                && !this.context.ValidatedIdentities.Contains(identityValue)
                                && !this.context.InvalidIdentities.Contains(identityValue))
                            {
                                var identities = await RetryHelper.RetryAsync(async () =>
                                {
                                    return await identityHttpClient.ReadIdentitiesAsync(IdentitySearchFilter.MailAddress, identityValue);
                                }, 5);

                                if (identities.Count > 0)
                                {
                                    context.ValidatedIdentities.Add(identityValue);
                                }
                                else
                                {
                                    Logger.LogTrace(LogDestination.File, $"Found identity {identityValue} in batch {batchContext.BatchId} which has not yet been validated for the target account");
                                    identitiesToProcess.Add(identityValue);
                                }
                            }
                        }
                    }
                }
            }

            Logger.LogInformation(LogDestination.File, $"Adding {identitiesToProcess.Count} identities to the account for batch {batchContext.BatchId}");
            foreach (var identity in identitiesToProcess)
            {
                try
                {
                    var createUserResult = await RetryHelper.RetryAsync(async () =>
                    {
                        return await graphClient.CreateUserAsync(new GraphUserPrincipalNameCreationContext()
                        {
                            PrincipalName = identity
                        });
                    }, 5);

                    // using identity from createUserResult since the identity could be in a mangled format that ReadIdentities does not support
                    var identities = await RetryHelper.RetryAsync(async () =>
                    {
                        return await identityHttpClient.ReadIdentitiesAsync(IdentitySearchFilter.MailAddress, createUserResult.MailAddress);
                    }, 5);

                    if (identities.Count == 0)
                    {
                        Logger.LogWarning(LogDestination.File, $"Unable to add identity {identity} to the target account for batch {batchContext.BatchId}");
                        context.InvalidIdentities.Add(identity);
                    }
                    else
                    {
                        var userEntitlement = new UserEntitlement
                        {
                            User = createUserResult,
                            AccessLevel = new AccessLevel
                            {
                                AccountLicenseType = AccountLicenseType.Express
                            }
                        };

                        var document = new SingleJsonDocumentBuilder().AddUserEntitlement(userEntitlement).Build();
                        var assignResult = await entitlementHttpClient.UpdateUserEntitlementsAsync(document, doNotSendInviteForNewUsers: true);
                        context.ValidatedIdentities.Add(identity);
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(LogDestination.File, ex, $"Unable to add identity {identity} to the target account for batch {batchContext.BatchId}");
                    context.InvalidIdentities.Add(identity);
                }
            }

            Logger.LogInformation(LogDestination.File, $"Completed adding {identitiesToProcess.Count} identities to the account for batch {batchContext.BatchId}");
        }
    }
}