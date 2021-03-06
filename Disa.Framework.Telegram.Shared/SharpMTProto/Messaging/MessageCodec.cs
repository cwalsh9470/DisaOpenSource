﻿// --------------------------------------------------------------------------------------------------------------------
// <copyright file="MessageCodec.cs">
//   Copyright (c) 2013-2014 Alexander Logger. All rights reserved.
// </copyright>
// --------------------------------------------------------------------------------------------------------------------

using System;
using System.Diagnostics;
using BigMath;
using BigMath.Utils;
using SharpMTProto.Schema;
using SharpMTProto.Annotations;
using SharpMTProto.Services;
using SharpTL;

namespace SharpMTProto.Messaging
{
    public interface IMessageCodec
    {
        /// <summary>
        ///     Encode as plain message.
        /// </summary>
        /// <param name="message">Message.</param>
        /// <returns>Serialized plain message.</returns>
        byte[] EncodePlainMessage([NotNull] IMessage message);

        /// <summary>
        ///     Decode plain message.
        /// </summary>
        /// <param name="messageBytes">Serialized message bytes.</param>
        /// <returns>Message.</returns>
        IMessage DecodePlainMessage(byte[] messageBytes);

        /// <summary>
        ///     Encode as encrypted message.
        /// </summary>
        /// <param name="message">A message.</param>
        /// <param name="authKey">
        ///     Authorization Key a 2048-bit key shared by the client device and the server, created upon user
        ///     registration directly on the client device be exchanging Diffie-Hellman keys, and never transmitted over a network.
        ///     Each authorization key is user-specific. There is nothing that prevents a user from having several keys (that
        ///     correspond to “permanent sessions” on different devices), and some of these may be locked forever in the event the
        ///     device is lost.
        /// </param>
        /// <param name="salt">
        ///     Server Salt is a (random) 64-bit number periodically (say, every 24 hours) changed (separately for
        ///     each session) at the request of the server. All subsequent messages must contain the new salt (although, messages
        ///     with the old salt are still accepted for a further 300 seconds). Required to protect against replay attacks and
        ///     certain tricks associated with adjusting the client clock to a moment in the distant future.
        /// </param>
        /// <param name="sessionId">
        ///     Session is a (random) 64-bit number generated by the client to distinguish between individual sessions (for
        ///     example, between different instances of the application, created with the same authorization key). The session in
        ///     conjunction with the key identifier corresponds to an application instance. The server can maintain session state.
        ///     Under no circumstances can a message meant for one session be sent into a different session. The server may
        ///     unilaterally forget any client sessions; clients should be able to handle this.
        /// </param>
        /// <param name="sender">Sender of the message.</param>
        /// <returns>Serialized encrypted message.</returns>
        byte[] EncodeEncryptedMessage([NotNull] IMessage message, [NotNull] byte[] authKey, ulong salt, ulong sessionId, Sender sender);

        /// <summary>
        ///     Decode encrypted message.
        /// </summary>
        /// <param name="messageBytes">Whole message bytes, which contain encrypted data.</param>
        /// <param name="authKey">
        ///     Authorization Key a 2048-bit key shared by the client device and the server, created upon user
        ///     registration directly on the client device be exchanging Diffie-Hellman keys, and never transmitted over a network.
        ///     Each authorization key is user-specific. There is nothing that prevents a user from having several keys (that
        ///     correspond to “permanent sessions” on different devices), and some of these may be locked forever in the event the
        ///     device is lost.
        /// </param>
        /// <param name="sender">Sender of the message.</param>
        /// <param name="salt">Salt.</param>
        /// <param name="sessionId">Session ID.</param>
        IMessage DecodeEncryptedMessage([NotNull] byte[] messageBytes, [NotNull] byte[] authKey, Sender sender, out UInt64 salt, out UInt64 sessionId);
    }

    public class MessageCodec : IMessageCodec
    {
        /// <summary>
        ///     Message header length in bytes.
        /// </summary>
        public const int PlainHeaderLength = 20;

        /// <summary>
        ///     Outer header length in bytes (8 + 16).
        /// </summary>
        private const int EncryptedOuterHeaderLength = 24;

        /// <summary>
        ///     Inner header length in bytes (8 + 8 + 8 + 4 + 4).
        /// </summary>
        private const int EncryptedInnerHeaderLength = 32;

        private const int MsgKeyLength = 16;
        private const int Alignment = 16;
        [ThreadStatic] private static byte[] _aesKeyAndIVComputationBuffer;
        private readonly byte[] _alignmentBuffer = new byte[Alignment];

        private readonly IEncryptionServices _encryptionServices;
        private readonly IHashServices _hashServices;
        private readonly IRandomGenerator _randomGenerator;
        private readonly TLRig _tlRig;

        /// <summary>
        ///     Initializes a new instance of the <see cref="MessageCodec" /> class.
        /// </summary>
        /// <exception cref="System.ArgumentNullException">The <paramref name="tlRig" /> is <c>null</c>.</exception>
        /// <exception cref="System.ArgumentNullException">The <paramref name="hashServices" /> is <c>null</c>.</exception>
        /// <exception cref="System.ArgumentNullException">The <paramref name="encryptionServices" /> is <c>null</c>.</exception>
        public MessageCodec([NotNull] TLRig tlRig, [NotNull] IHashServices hashServices, [NotNull] IEncryptionServices encryptionServices,
            IRandomGenerator randomGenerator)
        {
//            Argument.IsNotNull(() => tlRig);
//            Argument.IsNotNull(() => hashServices);
//            Argument.IsNotNull(() => encryptionServices);

            _tlRig = tlRig;
            _hashServices = hashServices;
            _encryptionServices = encryptionServices;
            _randomGenerator = randomGenerator;
        }

        public byte[] EncodePlainMessage(IMessage message)
        {
            byte[] serBody = _tlRig.Serialize(message.Body, TLSerializationMode.Boxed);

            int length = PlainHeaderLength + serBody.Length;
            var messageBytes = new byte[length];

            using (var streamer = new TLStreamer(messageBytes))
            {
                // Writing header.
                streamer.WriteInt64(0); // Plain unencrypted message must always have zero auth key id.
                streamer.WriteUInt64(message.MsgId); // MsgId.
                streamer.WriteInt32(serBody.Length); // Length.

                // Writing data.
                streamer.Write(serBody);
            }

            return messageBytes;
        }

        public IMessage DecodePlainMessage(byte[] messageBytes)
        {
            using (var streamer = new TLStreamer(messageBytes))
            {
                long authKey = streamer.ReadInt64();
                if (authKey != 0)
                {
                    throw new InvalidMessageException("Auth key must always be zero for a plain message.");
                }

                ulong msgId = streamer.ReadUInt64();
                int bodyLength = streamer.ReadInt32();
                if (bodyLength > streamer.BytesTillEnd)
                {
                    throw new InvalidMessageException("Wrong message body length.");
                }
                object body = _tlRig.Deserialize(streamer);
                return new Message(msgId, 0, body);
            }
        }

        public byte[] EncodeEncryptedMessage(IMessage message, byte[] authKey, ulong salt, ulong sessionId, Sender sender)
        {
            ulong authKeyId = ComputeAuthKeyId(authKey);
            byte[] serBody = _tlRig.Serialize(message.Body, TLSerializationMode.Boxed);

            int serBodyLength = serBody.Length;
            int innerDataLength = EncryptedInnerHeaderLength + serBodyLength;
            int mod = innerDataLength%Alignment;
            int paddingLength = mod > 0 ? Alignment - mod : 0;
            _randomGenerator.FillWithRandom(_alignmentBuffer);
            int innerDataWithPaddingLength = innerDataLength + paddingLength;

            int length = EncryptedOuterHeaderLength + innerDataWithPaddingLength;

            // Writing inner data.
            var innerDataWithPadding = new byte[innerDataWithPaddingLength];
            using (var streamer = new TLStreamer(innerDataWithPadding))
            {
                streamer.WriteUInt64(salt);
                streamer.WriteUInt64(sessionId);
                streamer.WriteUInt64(message.MsgId);
                streamer.WriteUInt32(message.Seqno);
                streamer.WriteInt32(serBodyLength);
                streamer.Write(serBody);
                streamer.Write(_alignmentBuffer, 0, paddingLength);
            }

            Int128 msgKey = ComputeMsgKey(new ArraySegment<byte>(innerDataWithPadding, 0, innerDataLength));

            // Encrypting.
            byte[] aesKey, aesIV;
            ComputeAesKeyAndIV(authKey, msgKey, out aesKey, out aesIV, sender);
            byte[] encryptedData = _encryptionServices.Aes256IgeEncrypt(innerDataWithPadding, aesKey, aesIV);

            Debug.Assert(encryptedData.Length == innerDataWithPaddingLength, "Wrong encrypted data length.");

            var messageBytes = new byte[length];
            using (var streamer = new TLStreamer(messageBytes))
            {
                // Writing header.
                streamer.WriteUInt64(authKeyId);
                streamer.WriteInt128(msgKey);

                // Writing encrypted data.
                streamer.Write(encryptedData, 0, innerDataWithPaddingLength);
            }
            return messageBytes;
        }

        public IMessage DecodeEncryptedMessage(byte[] messageBytes, byte[] authKey, Sender sender, out UInt64 salt, out UInt64 sessionId)
        {
            ulong providedAuthKeyId = ComputeAuthKeyId(authKey);

            var encryptedData = new byte[messageBytes.Length - EncryptedOuterHeaderLength];

            Int128 msgKey;

            using (var streamer = new TLStreamer(messageBytes))
            {
                // Reading header.
                ulong authKeyId = streamer.ReadUInt64();
                if (authKeyId != providedAuthKeyId)
                {
                    throw new InvalidAuthKey(string.Format("Message encrypted with auth key with id={0}, but auth key provided for decryption with id={1}.", authKeyId,
                        providedAuthKeyId));
                }
                msgKey = streamer.ReadInt128();

                // Reading encrypted data.
                streamer.Read(encryptedData, 0, encryptedData.Length);
            }

            // Decrypting.
            byte[] aesKey, aesIV;
            ComputeAesKeyAndIV(authKey, msgKey, out aesKey, out aesIV, sender);
            byte[] innerDataWithPadding = _encryptionServices.Aes256IgeDecrypt(encryptedData, aesKey, aesIV);

            Int32 msgDataLength;
            UInt64 msgId;
            UInt32 seqno;
            Object body;

            using (var streamer = new TLStreamer(innerDataWithPadding))
            {
                salt = streamer.ReadUInt64();
                sessionId = streamer.ReadUInt64();
                msgId = streamer.ReadUInt64();
                seqno = streamer.ReadUInt32();
                msgDataLength = streamer.ReadInt32();
                body = _tlRig.Deserialize(streamer);
            }

            int innerDataLength = EncryptedInnerHeaderLength + msgDataLength;

            // When an encrypted message is received, it must be checked that
            // msg_key is in fact equal to the 128 lower-order bits
            // of the SHA1 hash of the previously encrypted portion.
            Int128 expectedMsgKey = ComputeMsgKey(new ArraySegment<byte>(innerDataWithPadding, 0, innerDataLength));
            if (msgKey != expectedMsgKey)
            {
                throw new InvalidMessageException(string.Format("Expected message key to be {0}, but actual is {1}.", expectedMsgKey, msgKey));
            }

            return new Message(msgId, seqno, body);
        }

        private ulong ComputeAuthKeyId(byte[] authKey)
        {
            byte[] authKeySHA1 = _hashServices.ComputeSHA1(authKey);
            return authKeySHA1.ToUInt64(authKeySHA1.Length - 8, true);
        }

        private Int128 ComputeMsgKey(ArraySegment<byte> bytes)
        {
            byte[] innerDataSHA1 = _hashServices.ComputeSHA1(bytes);
            return innerDataSHA1.ToInt128(innerDataSHA1.Length - 16, true);
        }

        private void ComputeAesKeyAndIV(byte[] authKey, Int128 msgKey, out byte[] aesKey, out byte[] aesIV, Sender sender)
        {
            // x = 0 for messages from client to server and x = 8 for those from server to client.
            int x;
            switch (sender)
            {
                case Sender.Client:
                    x = 0;
                    break;
                case Sender.Server:
                    x = 8;
                    break;
                default:
                    throw new ArgumentOutOfRangeException("sender");
            }

            byte[] msgKeyBytes = msgKey.ToBytes();

            byte[] buffer = _aesKeyAndIVComputationBuffer ?? (_aesKeyAndIVComputationBuffer = new byte[32 + MsgKeyLength]);

            // sha1_a = SHA1 (msg_key + substr (auth_key, x, 32));
            Buffer.BlockCopy(msgKeyBytes, 0, buffer, 0, MsgKeyLength);
            Buffer.BlockCopy(authKey, x, buffer, MsgKeyLength, 32);
            byte[] sha1A = _hashServices.ComputeSHA1(buffer);

            // sha1_b = SHA1 (substr (auth_key, 32+x, 16) + msg_key + substr (auth_key, 48+x, 16));
            Buffer.BlockCopy(authKey, 32 + x, buffer, 0, 16);
            Buffer.BlockCopy(msgKeyBytes, 0, buffer, 16, MsgKeyLength);
            Buffer.BlockCopy(authKey, 48 + x, buffer, 16 + MsgKeyLength, 16);
            byte[] sha1B = _hashServices.ComputeSHA1(buffer);

            // sha1_с = SHA1 (substr (auth_key, 64+x, 32) + msg_key);
            Buffer.BlockCopy(authKey, 64 + x, buffer, 0, 32);
            Buffer.BlockCopy(msgKeyBytes, 0, buffer, 32, MsgKeyLength);
            byte[] sha1C = _hashServices.ComputeSHA1(buffer);

            // sha1_d = SHA1 (msg_key + substr (auth_key, 96+x, 32));
            Buffer.BlockCopy(msgKeyBytes, 0, buffer, 0, MsgKeyLength);
            Buffer.BlockCopy(authKey, 96 + x, buffer, MsgKeyLength, 32);
            byte[] sha1D = _hashServices.ComputeSHA1(buffer);

            // aes_key = substr (sha1_a, 0, 8) + substr (sha1_b, 8, 12) + substr (sha1_c, 4, 12);
            aesKey = new byte[32];
            Buffer.BlockCopy(sha1A, 0, aesKey, 0, 8);
            Buffer.BlockCopy(sha1B, 8, aesKey, 8, 12);
            Buffer.BlockCopy(sha1C, 4, aesKey, 20, 12);

            // aes_iv = substr (sha1_a, 8, 12) + substr (sha1_b, 0, 8) + substr (sha1_c, 16, 4) + substr (sha1_d, 0, 8);
            aesIV = new byte[32];
            Buffer.BlockCopy(sha1A, 8, aesIV, 0, 12);
            Buffer.BlockCopy(sha1B, 0, aesIV, 12, 8);
            Buffer.BlockCopy(sha1C, 16, aesIV, 20, 4);
            Buffer.BlockCopy(sha1D, 0, aesIV, 24, 8);
        }
    }
}
