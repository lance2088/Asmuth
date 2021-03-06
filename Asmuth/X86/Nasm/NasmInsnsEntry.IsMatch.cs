﻿using System;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Asmuth.X86.Nasm
{
	partial class NasmInsnsEntry
	{
		private enum NasmEncodingParsingState
		{
			Prefixes,
			PostSimdPrefix,
			Escape0F,
			PostEscape,
			PreOpcode = PostEscape,
			PostOpcode,
			PostModRM,
			Immediates
		}

		public bool Match(
			AddressSize defaultAddressSize, ImmutableLegacyPrefixList legacyPrefixes, Xex xex, byte opcode,
			out bool hasModRM, out int immediateSize)
		{
			var partialInstruction = new Instruction.Builder
			{
				DefaultAddressSize = defaultAddressSize,
				LegacyPrefixes = legacyPrefixes,
				Xex = xex,
				OpcodeByte = opcode
			}.Build();

			return Match(partialInstruction, upToOpcode: true, hasModRM: out hasModRM, immediateSize: out immediateSize);
		}

		public bool IsMatch(Instruction instruction)
		{
			bool hasModRM;
			int immediateSize;
			return Match(instruction, upToOpcode: false, hasModRM: out hasModRM, immediateSize: out immediateSize);
		}

		private bool Match(Instruction instruction, bool upToOpcode, out bool hasModRM, out int immediateSize)
		{
			hasModRM = false;
			immediateSize = 0;
			if (IsAssembleOnly || IsPseudo) return false;

			var expectedXexType = XexType.Escapes;
			var expectedOpcodeMap = OpcodeMap.Default;
			var state = NasmEncodingParsingState.Prefixes;
			foreach (var token in encodingTokens)
			{
				switch (token.Type)
				{
					// Address size
					case NasmEncodingTokenType.AddressSize_Fixed16:
						if (instruction.EffectiveAddressSize != AddressSize._16) return false;
						break;

					case NasmEncodingTokenType.AddressSize_Fixed32:
						if (instruction.EffectiveAddressSize != AddressSize._32) return false;
						break;

					case NasmEncodingTokenType.AddressSize_Fixed64:
						if (instruction.EffectiveAddressSize != AddressSize._64) return false;
						break;

					case NasmEncodingTokenType.AddressSize_NoOverride:
						if (instruction.EffectiveAddressSize != instruction.DefaultAddressSize) return false;
						break;

					// Operand size
					case NasmEncodingTokenType.OperandSize_16:
						if (GetIntegerOperandSize(instruction) != OperandSize.Word) return false;
						break;

					case NasmEncodingTokenType.OperandSize_32:
						if (GetIntegerOperandSize(instruction) != OperandSize.Dword) return false;
						break;

					case NasmEncodingTokenType.OperandSize_64:
						if (GetIntegerOperandSize(instruction) != OperandSize.Qword) return false;
						break;

					case NasmEncodingTokenType.OperandSize_NoOverride:
						if (instruction.LegacyPrefixes.HasOperandSizeOverride) return false;
						break;

					case NasmEncodingTokenType.OperandSize_64WithoutW:
						if (instruction.DefaultAddressSize != AddressSize._64
							|| instruction.LegacyPrefixes.HasOperandSizeOverride) return false;
						break;

					// Legacy prefixes
					case NasmEncodingTokenType.LegacyPrefix_F2:
						if (!instruction.LegacyPrefixes.Contains(LegacyPrefix.RepeatNotEqual)) return false;
						break;

					case NasmEncodingTokenType.LegacyPrefix_F3:
						if (!instruction.LegacyPrefixes.Contains(LegacyPrefix.RepeatEqual)) return false;
						break;

					case NasmEncodingTokenType.LegacyPrefix_NoF3:
						if (instruction.LegacyPrefixes.Contains(LegacyPrefix.RepeatEqual)) return false;
						break;

					case NasmEncodingTokenType.LegacyPrefix_NoSimd:
						if (instruction.LegacyPrefixes.ContainsFromGroup(LegacyPrefixGroup.Repeat)
							|| instruction.LegacyPrefixes.ContainsFromGroup(LegacyPrefixGroup.OperandSizeOverride))
							return false;
						break;

					case NasmEncodingTokenType.LegacyPrefix_MustRep:
						if (instruction.SimdPrefix != SimdPrefix._F3) return false;
						break;

					case NasmEncodingTokenType.LegacyPrefix_NoRep:
						if (instruction.LegacyPrefixes.ContainsFromGroup(LegacyPrefixGroup.Repeat)) return false;
						break;

					case NasmEncodingTokenType.LegacyPrefix_DisassembleRepAsRepE:
					case NasmEncodingTokenType.LegacyPrefix_HleAlways:
					case NasmEncodingTokenType.LegacyPrefix_HleWithLock:
					case NasmEncodingTokenType.LegacyPrefix_XReleaseAlways:
						break;

					// Vex
					case NasmEncodingTokenType.Vex:
						if (instruction.Xex.Type != vexEncoding.GetXexType()) return false;
						throw new NotImplementedException();

					// Rex
					case NasmEncodingTokenType.Rex_NoB:
						if (instruction.Xex.BaseRegExtension) return false;
						break;

					case NasmEncodingTokenType.Rex_NoW:
						if (instruction.Xex.OperandSize64) return false;
						break;

					case NasmEncodingTokenType.Rex_LockAsRexR: break;

					// Byte
					case NasmEncodingTokenType.Byte:
						if (state < NasmEncodingParsingState.PostSimdPrefix)
						{
							if (token.Byte == 0x66 || token.Byte == 0xF2 || token.Byte == 0xF3)
							{
								var legacyPrefix = LegacyPrefixEnum.TryFromEncodingByte(token.Byte).Value;
								if (!instruction.LegacyPrefixes.EndsWith(legacyPrefix)) return false;
								state = NasmEncodingParsingState.PostSimdPrefix;
								continue;
							}
						}

						if (state < NasmEncodingParsingState.Escape0F && token.Byte == 0x0F)
						{
							if (!instruction.Xex.Type.AllowsEscapes()) return false;
							expectedOpcodeMap = OpcodeMap.Escape0F;
							state = NasmEncodingParsingState.Escape0F;
							continue;
						}

						if (state == NasmEncodingParsingState.Escape0F && (token.Byte == 0x38 || token.Byte == 0x3A))
						{
							expectedOpcodeMap = token.Byte == 0x38 ? OpcodeMap.Escape0F38 : OpcodeMap.Escape0F3A;
							state = NasmEncodingParsingState.PostEscape;
							continue;
						}

						if (state < NasmEncodingParsingState.PostOpcode)
						{
							if (instruction.MainByte != token.Byte) return false;
							state = NasmEncodingParsingState.PostOpcode;
							continue;
						}

						if (state == NasmEncodingParsingState.PostOpcode)
						{
							if (!upToOpcode && (byte?)instruction.ModRM != token.Byte) return false;
							hasModRM = true;
							state = NasmEncodingParsingState.PostModRM;
							continue;
						}

						// Constant imm?
						throw new NotImplementedException();

					case NasmEncodingTokenType.Byte_PlusConditionCode:
					case NasmEncodingTokenType.Byte_PlusRegister:
					{
						if (state > NasmEncodingParsingState.PostOpcode)
							throw new NotImplementedException();

						byte mask = token.Type == NasmEncodingTokenType.Byte_PlusConditionCode ? (byte)0xF0 : (byte)0xF8;
						if ((instruction.MainByte & mask) != token.Byte) return false;
						state = NasmEncodingParsingState.PostOpcode;

						break;
					}

					// ModRM
					case NasmEncodingTokenType.ModRM:
						if (!upToOpcode && !instruction.ModRM.HasValue) return false;
						hasModRM = true;
						state = NasmEncodingParsingState.PostModRM;
						break;

					case NasmEncodingTokenType.ModRM_FixedReg:
						if (!upToOpcode && (!instruction.ModRM.HasValue || instruction.ModRM.Value.GetReg() != token.Byte)) return false;
						hasModRM = true;
						state = NasmEncodingParsingState.PostModRM;
						break;

					// VectorSib
					case NasmEncodingTokenType.VectorSib_XmmDwordIndices:
					case NasmEncodingTokenType.VectorSib_XmmQwordIndices:
					case NasmEncodingTokenType.VectorSib_YmmDwordIndices:
					case NasmEncodingTokenType.VectorSib_YmmQwordIndices:
					case NasmEncodingTokenType.VectorSib_ZmmDwordIndices:
					case NasmEncodingTokenType.VectorSib_ZmmQwordIndices:
						if (!instruction.Sib.HasValue) return false;
						break;

					// Immediates
					case NasmEncodingTokenType.Immediate_Byte:
					case NasmEncodingTokenType.Immediate_Byte_Signed:
					case NasmEncodingTokenType.Immediate_Byte_Unsigned:
					case NasmEncodingTokenType.Immediate_Is4:
					case NasmEncodingTokenType.Immediate_RelativeOffset8:
						immediateSize++;
						break;

					case NasmEncodingTokenType.Immediate_Word: immediateSize += 2; break;

					case NasmEncodingTokenType.Immediate_Dword:
					case NasmEncodingTokenType.Immediate_Dword_Signed:
						immediateSize += 4;
						break;

					case NasmEncodingTokenType.Immediate_Qword: immediateSize += 8; break;

					case NasmEncodingTokenType.Immediate_RelativeOffset:
						immediateSize += instruction.DefaultAddressSize == AddressSize._16 ? 2 : 4;
						break;

					// Misc
					case NasmEncodingTokenType.Misc_AssembleWaitPrefix:
					case NasmEncodingTokenType.Misc_NoHigh8Register:
						break;

					default:
						throw new NotImplementedException($"Nasm token {token}");
				}
			}

			foreach (var operand in operands)
			{
				if (operand.Field == OperandField.BaseReg)
				{
					var optype = operand.Type & NasmOperandType.OpType_Mask;
					var isReg = !instruction.ModRM.HasValue || (instruction.ModRM.Value & ModRM.Mod_Mask) == ModRM.Mod_Direct;
					if (optype == NasmOperandType.OpType_Register && !isReg) return false;
					if (optype == NasmOperandType.OpType_Memory && isReg) return false;
				}
			}
			
			return state >= NasmEncodingParsingState.PostOpcode
				&& (expectedXexType == XexType.Escapes ? instruction.Xex.Type.AllowsEscapes() : instruction.Xex.Type == expectedXexType)
				&& instruction.OpcodeMap == expectedOpcodeMap
				&& (upToOpcode || 
					(instruction.ModRM.HasValue == hasModRM
					&& instruction.ImmediateSizeInBytes == immediateSize));
		}

		private static OperandSize GetIntegerOperandSize(Instruction instruction)
		{
			if (instruction.DefaultAddressSize == AddressSize._64 && instruction.Xex.OperandSize64)
				return OperandSize.Qword;
			var operandSize = instruction.DefaultAddressSize == AddressSize._16 ? OperandSize.Word : OperandSize.Dword;
			return operandSize.OverrideWordDword(instruction.LegacyPrefixes.HasOperandSizeOverride);
		}
	}
}
