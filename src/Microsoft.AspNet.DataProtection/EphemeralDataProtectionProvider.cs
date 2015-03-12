﻿// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using Microsoft.AspNet.Cryptography.Cng;
using Microsoft.AspNet.DataProtection.AuthenticatedEncryption;
using Microsoft.AspNet.DataProtection.KeyManagement;
using Microsoft.Framework.Internal;
using Microsoft.Framework.Logging;

namespace Microsoft.AspNet.DataProtection
{
    /// <summary>
    /// An <see cref="IDataProtectionProvider"/> that is transient.
    /// </summary>
    /// <remarks>
    /// Payloads generated by a given <see cref="EphemeralDataProtectionProvider"/> instance can only
    /// be deciphered by that same instance. Once the instance is lost, all ciphertexts
    /// generated by that instance are permanently undecipherable.
    /// </remarks>
    public sealed class EphemeralDataProtectionProvider : IDataProtectionProvider
    {
        private readonly KeyRingBasedDataProtectionProvider _dataProtectionProvider;

        /// <summary>
        /// Creates an ephemeral <see cref="IDataProtectionProvider"/>.
        /// </summary>
        public EphemeralDataProtectionProvider()
            : this(services: null)
        {
        }

        /// <summary>
        /// Creates an ephemeral <see cref="IDataProtectionProvider"/>, optionally providing
        /// services (such as logging) for consumption by the provider.
        /// </summary>
        public EphemeralDataProtectionProvider(IServiceProvider services)
        {
            IKeyRingProvider keyringProvider;
            if (OSVersionUtil.IsWindows())
            {
                // Fastest implementation: AES-256-GCM [CNG]
                keyringProvider = new EphemeralKeyRing<CngGcmAuthenticatedEncryptionOptions>();
            }
            else
            {
                // Slowest implementation: AES-256-CBC + HMACSHA256 [Managed]
                keyringProvider = new EphemeralKeyRing<ManagedAuthenticatedEncryptionOptions>();
            }

            var logger = services.GetLogger<EphemeralDataProtectionProvider>();
            if (logger.IsWarningLevelEnabled())
            {
                logger.LogWarning("Using ephemeral data protection provider. Payloads will be undecipherable upon application shutdown.");
            }

            _dataProtectionProvider = new KeyRingBasedDataProtectionProvider(keyringProvider, services);
        }

        public IDataProtector CreateProtector([NotNull] string purpose)
        {
            // just forward to the underlying provider
            return _dataProtectionProvider.CreateProtector(purpose);
        }

        private sealed class EphemeralKeyRing<T> : IKeyRing, IKeyRingProvider
            where T : IInternalAuthenticatedEncryptionOptions, new()
        {
            // Currently hardcoded to a 512-bit KDK.
            private const int NUM_BYTES_IN_KDK = 512 / 8;

            public IAuthenticatedEncryptor DefaultAuthenticatedEncryptor { get; } = new T().ToConfiguration(services: null).CreateNewDescriptor().CreateEncryptorInstance();

            public Guid DefaultKeyId { get; } = default(Guid);

            public IAuthenticatedEncryptor GetAuthenticatedEncryptorByKeyId(Guid keyId, out bool isRevoked)
            {
                isRevoked = false;
                return (keyId == default(Guid)) ? DefaultAuthenticatedEncryptor : null;
            }

            public IKeyRing GetCurrentKeyRing()
            {
                return this;
            }
        }
    }
}
