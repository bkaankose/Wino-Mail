using SQLite;
using System;
using System.Collections.Generic;

namespace Wino.Core.Domain.Entities
{
    /// <summary>
    /// Back storage for simple name-address book.
    /// These values will be inserted during MIME fetch.
    /// </summary>


    // TODO: This can easily evolve to Contact store, just like People app in Windows 10/11.
    // Do it.
    public class AddressInformation : IEquatable<AddressInformation>
    {
        [PrimaryKey]
        public string Address { get; set; }
        public string Name { get; set; }

        public string DisplayName => Address == Name ? Address : $"{Name} <{Address}>";

        public override bool Equals(object obj)
        {
            return Equals(obj as AddressInformation);
        }

        public bool Equals(AddressInformation other)
        {
            return !(other is null) &&
                   Address == other.Address &&
                   Name == other.Name;
        }

        public override int GetHashCode()
        {
            int hashCode = -1717786383;
            hashCode = hashCode * -1521134295 + EqualityComparer<string>.Default.GetHashCode(Address);
            hashCode = hashCode * -1521134295 + EqualityComparer<string>.Default.GetHashCode(Name);
            return hashCode;
        }

        public static bool operator ==(AddressInformation left, AddressInformation right)
        {
            return EqualityComparer<AddressInformation>.Default.Equals(left, right);
        }

        public static bool operator !=(AddressInformation left, AddressInformation right)
        {
            return !(left == right);
        }
    }
}
