﻿using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PnP.Core.Services;

namespace PnP.Core.Auth.Services.Http
{
    /// <summary>
    /// Retry handler for Azure AD authentication requests via MSAL
    /// </summary>
    internal sealed class MsalRetryHandler : RetryHandlerBase
    {
        #region Construction
        public MsalRetryHandler(ILogger<RetryHandlerBase> log, IOptions<PnPGlobalSettingsOptions> globalSettings) : base(log, globalSettings?.Value)
        {
            Configure();
        }
        #endregion

        private void Configure()
        {
            if (GlobalSettings != null)
            {
                UseRetryAfterHeader = GlobalSettings.HttpAzureActiveDirectoryUseRetryAfterHeader;
                MaxRetries = GlobalSettings.HttpAzureActiveDirectoryMaxRetries;
                DelayInSeconds = GlobalSettings.HttpAzureActiveDirectoryDelayInSeconds;
                IncrementalDelay = GlobalSettings.HttpAzureActiveDirectoryUseIncrementalDelay;
            }
        }

    }
}
