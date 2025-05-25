using System;
using System.Collections.Generic;
using Solnet.Wallet;
using System.IO;
using System.Linq;

namespace Solnet.Rpc.Models
{
    

    public class AddressLookupTableAccount
    {
        public PublicKey Key { get; set; }
        public AddressLookupTableState State { get; set; }

        public AddressLookupTableAccount(PublicKey key, AddressLookupTableState state)
        {
            Key = key;
            State = state;
        }

        public bool IsActive()
        {
            const ulong U64_MAX = ulong.MaxValue;
            return State.DeactivationSlot == U64_MAX;
        }

        public static AddressLookupTableState Deserialize(byte[] accountData)
        {
            // Layout constants
            const int LOOKUP_TABLE_META_SIZE = 56;
            if (accountData.Length < LOOKUP_TABLE_META_SIZE)
                throw new ArgumentException("accountData too short.");

            // Read meta
            var meta = LookupTableMetaLayout.Decode(accountData);

            // Read addresses
            int serializedAddressesLen = accountData.Length - LOOKUP_TABLE_META_SIZE;
            if (serializedAddressesLen < 0 || serializedAddressesLen % 32 != 0)
                throw new InvalidDataException("lookup table is invalid");

            int numSerializedAddresses = serializedAddressesLen / 32;
            var addresses = new List<PublicKey>();
            for (int i = 0; i < numSerializedAddresses; i++)
            {
                int offset = LOOKUP_TABLE_META_SIZE + i * 32;
                addresses.Add(new PublicKey(accountData.Skip(offset).Take(32).ToArray()));
            }

            return new AddressLookupTableState
            {
                DeactivationSlot = meta.DeactivationSlot,
                LastExtendedSlot = meta.LastExtendedSlot,
                LastExtendedSlotStartIndex = meta.LastExtendedStartIndex,
                Authority = meta.Authority != null && meta.Authority.Length == 32
                    ? new PublicKey(meta.Authority)
                    : null,
                Addresses = addresses
            };
        }
    }

    public class LookupTableMeta
    {
        public uint TypeIndex { get; set; }
        public ulong DeactivationSlot { get; set; }
        public ulong LastExtendedSlot { get; set; }
        public byte LastExtendedStartIndex { get; set; }
        public byte[]? Authority { get; set; }
    }

    public static class LookupTableMetaLayout
    {
        public static LookupTableMeta Decode(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var reader = new BinaryReader(ms);

            // typeIndex (u32 LE)
            uint typeIndex = reader.ReadUInt32();

            // deactivationSlot (u64 LE)
            ulong deactivationSlot = reader.ReadUInt64();

            // lastExtendedSlot (u64 LE)
            ulong lastExtendedSlot = reader.ReadUInt64();

            // lastExtendedStartIndex (u8)
            byte lastExtendedStartIndex = reader.ReadByte();

            // option (u8): 0 = none, 1 = present
            byte option = reader.ReadByte();
            byte[]? authority = null;
            if (option == 1)
            {
                authority = reader.ReadBytes(32);
            }

            return new LookupTableMeta
            {
                TypeIndex = typeIndex,
                DeactivationSlot = deactivationSlot,
                LastExtendedSlot = lastExtendedSlot,
                LastExtendedStartIndex = lastExtendedStartIndex,
                Authority = authority
            };
        }
    }
}