// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.Versioning;
using Internal.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace System.Security.Cryptography
{
    public sealed partial class ECDsaOpenSsl : ECDsa, IRuntimeAlgorithm
    {
        // secp521r1 maxes out at 139 bytes, so 256 should always be enough
        private const int SignatureStackBufSize = 256;

        private ECOpenSsl? _key;

        /// <summary>
        /// Create an ECDsaOpenSsl algorithm with a named curve.
        /// </summary>
        /// <param name="curve">The <see cref="ECCurve"/> representing the curve.</param>
        /// <exception cref="ArgumentNullException">if <paramref name="curve" /> is null.</exception>
        [UnsupportedOSPlatform("android")]
        [UnsupportedOSPlatform("browser")]
        [UnsupportedOSPlatform("ios")]
        [UnsupportedOSPlatform("tvos")]
        [UnsupportedOSPlatform("windows")]
        public ECDsaOpenSsl(ECCurve curve)
        {
            ThrowIfNotSupported();
            _key = new ECOpenSsl(curve);
            ForceSetKeySize(_key.KeySize);
        }

        /// <summary>
        ///     Create an ECDsaOpenSsl algorithm with a random 521 bit key pair.
        /// </summary>
        [UnsupportedOSPlatform("android")]
        [UnsupportedOSPlatform("browser")]
        [UnsupportedOSPlatform("ios")]
        [UnsupportedOSPlatform("tvos")]
        [UnsupportedOSPlatform("windows")]
        public ECDsaOpenSsl()
            : this(521)
        {
        }

        /// <summary>
        ///     Creates a new ECDsaOpenSsl object that will use a randomly generated key of the specified size.
        /// </summary>
        /// <param name="keySize">Size of the key to generate, in bits.</param>
        [UnsupportedOSPlatform("android")]
        [UnsupportedOSPlatform("browser")]
        [UnsupportedOSPlatform("ios")]
        [UnsupportedOSPlatform("tvos")]
        [UnsupportedOSPlatform("windows")]
        public ECDsaOpenSsl(int keySize)
        {
            ThrowIfNotSupported();
            // Use the base setter to get the validation and field assignment without the
            // side effect of dereferencing _key.
            base.KeySize = keySize;
            _key = new ECOpenSsl(this);
        }

        /// <summary>
        /// Set the KeySize without validating against LegalKeySizes.
        /// </summary>
        /// <param name="newKeySize">The value to set the KeySize to.</param>
        private void ForceSetKeySize(int newKeySize)
        {
            // In the event that a key was loaded via ImportParameters, curve name, or an IntPtr/SafeHandle
            // it could be outside of the bounds that we currently represent as "legal key sizes".
            // Since that is our view into the underlying component it can be detached from the
            // component's understanding.  If it said it has opened a key, and this is the size, trust it.
            KeySizeValue = newKeySize;
        }

        // Return the three sizes that can be explicitly set (for backwards compatibility)
        public override KeySizes[] LegalKeySizes => s_defaultKeySizes.CloneKeySizesArray();

        public override byte[] SignHash(byte[] hash)
        {
            ArgumentNullException.ThrowIfNull(hash);

            ThrowIfDisposed();
            SafeEcKeyHandle key = _key.Value;
            int signatureLength = Interop.Crypto.EcDsaSize(key);

            Span<byte> signDestination = stackalloc byte[SignatureStackBufSize];
            ReadOnlySpan<byte> derSignature = SignHash(hash, signDestination, signatureLength, key);

            byte[] converted = AsymmetricAlgorithmHelpers.ConvertDerToIeee1363(derSignature, KeySize);
            return converted;
        }

        public override bool TrySignHash(ReadOnlySpan<byte> hash, Span<byte> destination, out int bytesWritten)
        {
            return TrySignHashCore(
                hash,
                destination,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation,
                out bytesWritten);
        }

        protected override bool TrySignHashCore(
            ReadOnlySpan<byte> hash,
            Span<byte> destination,
            DSASignatureFormat signatureFormat,
            out int bytesWritten)
        {
            ThrowIfDisposed();
            SafeEcKeyHandle key = _key.Value;

            int signatureLength = Interop.Crypto.EcDsaSize(key);
            Span<byte> signDestination = stackalloc byte[SignatureStackBufSize];

            if (signatureFormat == DSASignatureFormat.IeeeP1363FixedFieldConcatenation)
            {
                int encodedSize = 2 * AsymmetricAlgorithmHelpers.BitsToBytes(KeySize);

                if (destination.Length < encodedSize)
                {
                    bytesWritten = 0;
                    return false;
                }

                ReadOnlySpan<byte> derSignature = SignHash(hash, signDestination, signatureLength, key);
                bytesWritten = AsymmetricAlgorithmHelpers.ConvertDerToIeee1363(derSignature, KeySize, destination);
                Debug.Assert(bytesWritten == encodedSize);
                return true;
            }
            else if (signatureFormat == DSASignatureFormat.Rfc3279DerSequence)
            {
                if (destination.Length >= signatureLength)
                {
                    signDestination = destination;
                }
                else if (signatureLength > signDestination.Length)
                {
                    Debug.Fail($"Stack-based signDestination is insufficient ({signatureLength} needed)");
                    bytesWritten = 0;
                    return false;
                }

                ReadOnlySpan<byte> derSignature = SignHash(hash, signDestination, signatureLength, key);

                if (destination == signDestination)
                {
                    bytesWritten = derSignature.Length;
                    return true;
                }

                return Helpers.TryCopyToDestination(derSignature, destination, out bytesWritten);
            }
            else
            {
                throw new ArgumentOutOfRangeException(nameof(signatureFormat));
            }
        }

        private static ReadOnlySpan<byte> SignHash(
            ReadOnlySpan<byte> hash,
            Span<byte> destination,
            int signatureLength,
            SafeEcKeyHandle key)
        {
            if (signatureLength > destination.Length)
            {
                Debug.Fail($"Stack-based signDestination is insufficient ({signatureLength} needed)");
                destination = new byte[signatureLength];
            }

            if (!Interop.Crypto.EcDsaSign(hash, destination, out int actualLength, key))
            {
                throw Interop.Crypto.CreateOpenSslCryptographicException();
            }

            Debug.Assert(
                actualLength <= signatureLength,
                "ECDSA_sign reported an unexpected signature size",
                "ECDSA_sign reported signatureSize was {0}, when <= {1} was expected",
                actualLength,
                signatureLength);

            return destination.Slice(0, actualLength);
        }

        public override bool VerifyHash(byte[] hash, byte[] signature)
        {
            ArgumentNullException.ThrowIfNull(hash);
            ArgumentNullException.ThrowIfNull(signature);

            return VerifyHash((ReadOnlySpan<byte>)hash, (ReadOnlySpan<byte>)signature);
        }

        public override bool VerifyHash(ReadOnlySpan<byte> hash, ReadOnlySpan<byte> signature) =>
            VerifyHashCore(hash, signature, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        protected override bool VerifyHashCore(
            ReadOnlySpan<byte> hash,
            ReadOnlySpan<byte> signature,
            DSASignatureFormat signatureFormat)
        {
            ThrowIfDisposed();

            Span<byte> derSignature = stackalloc byte[SignatureStackBufSize];
            ReadOnlySpan<byte> toVerify = derSignature;

            if (signatureFormat == DSASignatureFormat.IeeeP1363FixedFieldConcatenation)
            {
                // The signature format for .NET is r.Concat(s). Each of r and s are of length BitsToBytes(KeySize), even
                // when they would have leading zeroes.  If it's the correct size, then we need to encode it from
                // r.Concat(s) to SEQUENCE(INTEGER(r), INTEGER(s)), because that's the format that OpenSSL expects.
                int expectedBytes = 2 * AsymmetricAlgorithmHelpers.BitsToBytes(KeySize);
                if (signature.Length != expectedBytes)
                {
                    // The input isn't of the right length, so we can't sensibly re-encode it.
                    return false;
                }

                if (AsymmetricAlgorithmHelpers.TryConvertIeee1363ToDer(signature, derSignature, out int derSize))
                {
                    toVerify = derSignature.Slice(0, derSize);
                }
                else
                {
                    toVerify = AsymmetricAlgorithmHelpers.ConvertIeee1363ToDer(signature);
                }
            }
            else if (signatureFormat == DSASignatureFormat.Rfc3279DerSequence)
            {
                toVerify = signature;
            }
            else
            {
                Debug.Fail($"Missing internal implementation handler for signature format {signatureFormat}");
                throw new CryptographicException(
                    SR.Cryptography_UnknownSignatureFormat,
                    signatureFormat.ToString());
            }

            SafeEcKeyHandle key = _key.Value;
            int verifyResult = Interop.Crypto.EcDsaVerify(hash, toVerify, key);
            return verifyResult == 1;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _key?.Dispose();
                _key = null;
            }

            base.Dispose(disposing);
        }

        public override int KeySize
        {
            get
            {
                return base.KeySize;
            }
            set
            {
                if (KeySize == value)
                    return;

                // Set the KeySize before FreeKey so that an invalid value doesn't throw away the key
                base.KeySize = value;

                ThrowIfDisposed();
                _key.Dispose();
                _key = new ECOpenSsl(this);
            }
        }

        public override void GenerateKey(ECCurve curve)
        {
            ThrowIfDisposed();
            _key.GenerateKey(curve);

            // Use ForceSet instead of the property setter to ensure that LegalKeySizes doesn't interfere
            // with the already loaded key.
            ForceSetKeySize(_key.KeySize);
        }

        public override void ImportParameters(ECParameters parameters)
        {
            ThrowIfDisposed();
            _key.ImportParameters(parameters);
            ForceSetKeySize(_key.KeySize);
        }

        public override ECParameters ExportExplicitParameters(bool includePrivateParameters)
        {
            ThrowIfDisposed();
            return ECOpenSsl.ExportExplicitParameters(_key.Value, includePrivateParameters);
        }

        public override ECParameters ExportParameters(bool includePrivateParameters)
        {
            ThrowIfDisposed();
            return ECOpenSsl.ExportParameters(_key.Value, includePrivateParameters);
        }

        public override void ImportEncryptedPkcs8PrivateKey(
            ReadOnlySpan<byte> passwordBytes,
            ReadOnlySpan<byte> source,
            out int bytesRead)
        {
            ThrowIfDisposed();
            base.ImportEncryptedPkcs8PrivateKey(passwordBytes, source, out bytesRead);
        }

        public override void ImportEncryptedPkcs8PrivateKey(
            ReadOnlySpan<char> password,
            ReadOnlySpan<byte> source,
            out int bytesRead)
        {
            ThrowIfDisposed();
            base.ImportEncryptedPkcs8PrivateKey(password, source, out bytesRead);
        }

        [MemberNotNull(nameof(_key))]
        private void ThrowIfDisposed()
        {
            if (_key == null)
            {
                throw new ObjectDisposedException(nameof(ECDsaOpenSsl));
            }
        }

        static partial void ThrowIfNotSupported();
    }
}
