using System;
using System.Collections.Generic;
using Solnet.Wallet;
using System.IO;
using System.Linq;

namespace Solnet.Rpc.Models
{
    public class AddressLookupTableState
    {
        public ulong DeactivationSlot { get; set; }
        public ulong LastExtendedSlot { get; set; }
        public byte LastExtendedSlotStartIndex { get; set; }
        public PublicKey? Authority { get; set; }
        public List<PublicKey> Addresses { get; set; } = new List<PublicKey>();
    }

    
}