using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace OrisTpsWriter.Core
{
    /// <summary>Clarion TopSpeed field types.</summary>
    public static class FieldType
    {
        public const byte Byte    = 0x01;
        public const byte Short   = 0x02;
        public const byte UShort  = 0x03;
        public const byte Date    = 0x04;
        public const byte Time    = 0x05;
        public const byte Long    = 0x06;
        public const byte ULong   = 0x07;
        public const byte Float   = 0x08;
        public const byte Double  = 0x09;
        public const byte Bcd     = 0x0A;
        public const byte String  = 0x12;
        public const byte CString = 0x13;
        public const byte PString = 0x14;
        public const byte Group   = 0x16;
    }

    /// <summary>
    /// ცხრილის ერთი ფილდის აღწერა (FieldDefinitionRecord).
    /// </summary>
    public class TpsField
    {
        public string Name { get; }          // "TBL:FIELDNAME"
        public byte Type { get; }
        public int Offset { get; }
        public int Length { get; }
        public int Elements { get; }
        public int Flags { get; }
        public int Index { get; set; }       // display sequence number (0,1,2,...)

        public TpsField(string name, byte type, int offset, int length,
                        int elements = 1, int flags = 0, int index = 0)
        {
            Name = name;
            Type = type;
            Offset = offset;
            Length = length;
            Elements = elements;
            Flags = flags;
            Index = index;
        }

        /// <summary>FieldDefinitionRecord binary serialization.</summary>
        public byte[] Serialize()
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);

            w.Write(Type);                                  // type (1)
            w.Write((ushort)Offset);                        // offset (2)
            w.Write(Encoding.ASCII.GetBytes(Name));         // name
            w.Write((byte)0);                               // null terminator
            w.Write((ushort)Elements);                      // elements (2)
            w.Write((ushort)Length);                        // length (2)
            w.Write((ushort)Flags);                         // flags (2)
            w.Write((ushort)Index);                         // index (2)

            // type-specific extras
            if (Type == FieldType.Bcd)
            {
                w.Write((byte)0); // bcdDigitsAfterDecimalPoint
                w.Write((byte)0); // bcdLengthOfElement
            }
            else if (Type == FieldType.String || Type == FieldType.CString || Type == FieldType.PString)
            {
                w.Write((ushort)Length); // stringLength
                w.Write((byte)0);        // empty stringMask
                w.Write((byte)0);        // extra byte when mask empty
            }

            return ms.ToArray();
        }
    }

    /// <summary>
    /// ცხრილის ერთი ინდექსის/key-ის აღწერა (IndexDefinitionRecord).
    ///
    /// რეალური Clarion format (verified against ARN.TPS):
    ///   externalFile (zero-terminated)
    ///   if empty: marker byte 0x01
    ///   name (zero-terminated)
    ///   flags (uint8)
    ///   fieldsInKey (uint16)
    ///   keyField[fieldsInKey] (uint16 each)      ← SEPARATE arrays!
    ///   keyFieldFlag[fieldsInKey] (uint16 each)
    /// </summary>
    public class TpsIndex
    {
        public string Name { get; }
        public string ExternalFile { get; }
        public int Flags { get; }
        public List<(int FieldIndex, int FieldFlag)> KeyFields { get; }

        public TpsIndex(string name, List<(int, int)> keyFields, int flags = 6, string externalFile = "")
        {
            Name = name;
            ExternalFile = externalFile;
            Flags = flags;
            KeyFields = keyFields ?? new List<(int, int)>();
        }

        public byte[] Serialize()
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);

            // externalFile (zero-terminated)
            w.Write(Encoding.ASCII.GetBytes(ExternalFile));
            w.Write((byte)0);
            // marker byte when externalFile empty
            if (ExternalFile.Length == 0)
                w.Write((byte)0x01);
            // name (zero-terminated)
            w.Write(Encoding.ASCII.GetBytes(Name));
            w.Write((byte)0);
            // flags
            w.Write((byte)Flags);
            // fieldsInKey
            w.Write((ushort)KeyFields.Count);
            // SEPARATE arrays: all keyField, then all keyFieldFlag
            foreach (var (fieldIndex, _) in KeyFields)
                w.Write((ushort)fieldIndex);
            foreach (var (_, fieldFlag) in KeyFields)
                w.Write((ushort)fieldFlag);

            return ms.ToArray();
        }
    }
}
