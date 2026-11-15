// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Security/AuthToken.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: one token instance per listener instead of process-wide static state;
// discovery publishing moved to DiscoveryPublisher.

using System;
using System.Security.Cryptography;

namespace B2M.Revit.Core.Security
{
    /// <summary>A random 256-bit token one listener accepts. Compared in constant time.</summary>
    public sealed class SessionToken
    {
        public string Value { get; }

        private SessionToken(string value)
        {
            Value = value;
        }

        public static SessionToken Generate()
        {
            var bytes = new byte[32];
#if NET5_0_OR_GREATER
            RandomNumberGenerator.Fill(bytes);
#else
            using (var rng = new RNGCryptoServiceProvider()) rng.GetBytes(bytes);
#endif
            return new SessionToken(Convert.ToBase64String(bytes));
        }

        public bool Verify(string candidate)
        {
            if (string.IsNullOrEmpty(candidate) || candidate.Length != Value.Length) return false;
            var diff = 0;
            for (var i = 0; i < Value.Length; i++) diff |= Value[i] ^ candidate[i];
            return diff == 0;
        }
    }
}
