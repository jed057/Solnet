
using Solnet.Rpc.Builders;
using Solnet.Rpc.Utilities;
using Solnet.Wallet;
using Solnet.Wallet.Utilities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using static Solnet.Rpc.Models.Message;

namespace Solnet.Rpc.Models
{
    /// <summary>
    /// Represents a Transaction in Solana.
    /// </summary>
    public class VersionedTransaction : Transaction
    {

        /// <summary>
        /// Address Table Lookups
        /// </summary>
        public List<MessageAddressTableLookup> AddressTableLookups { get; set; }

        /// <summary>
        /// Signs the transaction's message with the passed list of signers and adds them to the transaction, serializing it.
        /// </summary>
        /// <param name="signers">The list of signers.</param>
        /// <param name="altAccounts">The address lookup tables.</param>
        /// <returns>The serialized transaction.</returns>
        public byte[] Build(IList<Account> signers, List<AddressLookupTableState> altAccounts)
        {
            Sign(signers, altAccounts);

            return Serialize(null);
        }

        public byte[] Serialize(List<AddressLookupTableState> altAccounts)
        {
            byte[] signaturesLength = ShortVectorEncoding.EncodeLength(Signatures.Count);
            byte[] serializedMessage = CompileMessage(altAccounts);
            MemoryStream buffer = new(signaturesLength.Length + Signatures.Count * TransactionBuilder.SignatureLength +
                                      serializedMessage.Length);

            buffer.Write(signaturesLength);
            foreach (SignaturePubKeyPair signaturePair in Signatures)
            {
                buffer.Write(signaturePair.Signature);
            }

            buffer.Write(serializedMessage);
            return buffer.ToArray();
        }
        public bool Sign(IList<Account> signers, List<AddressLookupTableState> altAccounts)
        {
            Signatures ??= new List<SignaturePubKeyPair>();
            IEnumerable<Account> uniqueSigners = DeduplicateSigners(signers);
            byte[] serializedMessage = CompileMessage(altAccounts);

            foreach (Account account in uniqueSigners)
            {
                byte[] signatureBytes = account.Sign(serializedMessage);
                Signatures.Add(new SignaturePubKeyPair { PublicKey = account.PublicKey, Signature = signatureBytes });
            }

            return VerifySignatures(null);
        }

        public bool VerifySignatures(List<AddressLookupTableState> altAccounts) => VerifySignatures(CompileMessage(altAccounts));
        
        /// <summary>
        /// Compile the transaction data.
        /// </summary>
        public byte[] CompileMessage(List<AddressLookupTableState> altAccounts)
        {
            VersionedMessageBuilder messageBuilder = new() { FeePayer = FeePayer,
                //AccountKeys = _accountKeys 
            };

            if (RecentBlockHash != null) messageBuilder.RecentBlockHash = RecentBlockHash;
            if (NonceInformation != null) messageBuilder.NonceInformation = NonceInformation;

            foreach (var instruction in Instructions)
            {
                messageBuilder.AddInstruction((VersionedTransactionInstruction)instruction);
            }

            // start  alt map
            if (altAccounts != null)
            {
                // step 1: mark writable or readonly for each of ALTs based on keys of each instruction
                for (int i = 0; i < AddressTableLookups.Count; i++)
                {
                    var altAddrs = altAccounts[i].Addresses.Select(i => i.Key).ToList();
                    
                    HashSet<int> wIndex = [], rIndex = [];
                    foreach (var instr in Instructions)
                    {
                        foreach (var key in instr.Keys)
                        {
                            var ind = altAddrs.IndexOf(key.PublicKey);
                            if (ind >= 0)
                            {
                                if (key.PublicKey == Encoders.Base58.EncodeData(instr.ProgramId) ||
                                    key.IsSigner)
                                    continue;

                                if (key.IsWritable)
                                { wIndex.Add(ind); }
                                else
                                { rIndex.Add(ind); }
                            }
                        }
                    }
                    AddressTableLookups[i].WritableIndexes = wIndex.Order().Select(i => (byte)i).ToArray();
                    AddressTableLookups[i].ReadonlyIndexes = rIndex.Order().Select(i => (byte)i).ToArray();
                    
                }

                // step 2: build full account key list
                // fee payer + all signers + all program keys + all w/r in alts
                var fullAccountKeyList = new List<string> { FeePayer };
                fullAccountKeyList.AddRange(messageBuilder.AccountKeys);
                for (int i = 0; i < AddressTableLookups.Count; i++)
                {
                    foreach (var wind in AddressTableLookups[i].WritableIndexes)
                    {
                        fullAccountKeyList.Add(altAccounts[i].Addresses[wind]);
                    }
                    foreach (var rind in AddressTableLookups[i].ReadonlyIndexes)
                    {
                        fullAccountKeyList.Add(altAccounts[i].Addresses[rind]);
                    }
                }

                // step 3: fill out key indices for each instruction based on the full account key list
                // note: remove all keys in instruction except the signers
                foreach (var instruction in Instructions)
                {
                    var indices = new List<byte>();
                    var versionedIns = instruction as VersionedTransactionInstruction;
                    foreach (var key in versionedIns.Keys)
                    {
                        var index = fullAccountKeyList.IndexOf(key.PublicKey);
                        if (index == -1) throw new Exception("index not found");
                        indices.Add((byte)index);
                    }
                    versionedIns.KeyIndices = indices.ToArray();

                    versionedIns.Keys = versionedIns.Keys.Where(i => i.IsSigner).ToArray();
                }
            }
            // end alt map

            messageBuilder.AddressTableLookups = AddressTableLookups;

            return messageBuilder.Build();
        }

        /// <summary>
        /// Populate the Transaction from the given message and signatures.
        /// </summary>
        /// <param name="message">The <see cref="Message"/> object.</param>
        /// <param name="signatures">The list of signatures.</param>
        /// <returns>The Transaction object.</returns>
        public static VersionedTransaction Populate(VersionedMessage message, IList<byte[]> signatures = null)
        {
            VersionedTransaction tx = new()
            {
                RecentBlockHash = message.RecentBlockhash,
                Signatures = new List<SignaturePubKeyPair>(),
                Instructions = new List<TransactionInstruction>(),
                AddressTableLookups = message.AddressTableLookups,
                _accountKeys = message.AccountKeys
            };

            if (message.Header.RequiredSignatures > 0)
            {
                tx.FeePayer = message.AccountKeys[0];
            }

            if (signatures != null)
            {
                for (int i = 0; i < signatures.Count; i++)
                {
                    tx.Signatures.Add(new SignaturePubKeyPair
                    {
                        PublicKey = message.AccountKeys[i],
                        Signature = signatures[i]
                    });
                }
            }

            for (int i = 0; i < message.Instructions.Count; i++)
            {
                CompiledInstruction compiledInstruction = message.Instructions[i];
                (int accountLength, _) = ShortVectorEncoding.DecodeLength(compiledInstruction.KeyIndicesCount);

                List<AccountMeta> accounts = new(accountLength);
                for (int j = 0; j < accountLength; j++)
                {
                    int k = compiledInstruction.KeyIndices[j];
                    if (k >= message.AccountKeys.Count) continue;
                    accounts.Add(new AccountMeta(message.AccountKeys[k], message.IsAccountWritable(k),
                    tx.Signatures.Any(pair => pair.PublicKey.Key == message.AccountKeys[k].Key) || message.IsAccountSigner(k)));
                }

                VersionedTransactionInstruction instruction = new()
                {
                    Keys = accounts,
                    KeyIndices = compiledInstruction.KeyIndices,
                    ProgramId = message.AccountKeys[compiledInstruction.ProgramIdIndex],
                    Data = compiledInstruction.Data
                };
                if (i == 0 && accounts.Any(a => a.PublicKey == "SysvarRecentB1ockHashes11111111111111111111"))
                {
                    tx.NonceInformation = new NonceInformation { Instruction = instruction, Nonce = tx.RecentBlockHash };
                    continue;
                }
                tx.Instructions.Add(instruction);
            }

            return tx;
        }

        /// <summary>
        /// Populate the Transaction from the given compiled message and signatures.
        /// </summary>
        /// <param name="message">The compiled message, as base-64 encoded string.</param>
        /// <param name="signatures">The list of signatures.</param>
        /// <returns>The Transaction object.</returns>
        public static new VersionedTransaction Populate(string message, IList<byte[]> signatures = null)
            => Populate(VersionedMessage.Deserialize(message), signatures);

        /// <summary>
        /// Deserialize a wire format transaction into a Transaction object.
        /// </summary>
        /// <param name="data">The data to deserialize into the Transaction object.</param>
        /// <returns>The Transaction object.</returns>
        public static new VersionedTransaction Deserialize(ReadOnlySpan<byte> data)
        {
            // Read number of signatures
            (int signaturesLength, int encodedLength) =
                ShortVectorEncoding.DecodeLength(data[..ShortVectorEncoding.SpanLength]);
            List<byte[]> signatures = new(signaturesLength);

            for (int i = 0; i < signaturesLength; i++)
            {
                ReadOnlySpan<byte> signature =
                    data.Slice(encodedLength + (i * TransactionBuilder.SignatureLength),
                        TransactionBuilder.SignatureLength);
                signatures.Add(signature.ToArray());
            }

            var message = VersionedMessage.Deserialize(data[(encodedLength + (signaturesLength * TransactionBuilder.SignatureLength))..]);
            return Populate(message, signatures);
        }

        /// <summary>
        /// Deserialize a transaction encoded as base-64 into a Transaction object.
        /// </summary>
        /// <param name="data">The data to deserialize into the Transaction object.</param>
        /// <returns>The Transaction object.</returns>
        /// <exception cref="ArgumentNullException">Thrown when the given string is null.</exception>
        public static new VersionedTransaction Deserialize(string data)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));

            byte[] decodedBytes;

            try
            {
                decodedBytes = Convert.FromBase64String(data);
            }
            catch (Exception ex)
            {
                throw new Exception("could not decode transaction data from base64", ex);
            }

            return Deserialize(decodedBytes);
        }
    }
}

