﻿using System;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Asmuth.X86.Nasm
{
	/// <summary>
	/// Provides methods for manipulating instruction definitions in NASM's insns.dat format.
	/// </summary>
	public static class NasmInsns
	{
		private static readonly Regex instructionLineColumnRegex = new Regex(
			@"(\[[^\]]*\]|\S.*?)(?=(\s|\Z))", RegexOptions.CultureInvariant);

		private static readonly Regex codeStringColumnRegex = new Regex(
			@"\A\[
				(
					(?<operand_fields>[a-z-+]+):
					((?<evex_tuple_type>[a-z0-9]+):)?
				)?
				\s*
				(?<encoding>[^\]\r\n\t]+?)
			\s*\]\Z", RegexOptions.IgnorePatternWhitespace | RegexOptions.ExplicitCapture | RegexOptions.CultureInvariant);

		public static ICollection<string> PseudoInstructionMnemonics = new[]
		{
			"DB", "DW", "DD", "DQ", "DT", "DO", "DY", "DZ",
			"RESB", "RESW", "RESD", "RESQ", "REST", "RESO", "RESY", "RESZ",
		};

		public static IEnumerable<NasmInsnsEntry> Read(TextReader textReader)
		{
			Contract.Requires(textReader != null);

			while (true)
			{
				var line = textReader.ReadLine();
				if (line == null) break;

				if (IsIgnoredLine(line)) continue;
				yield return ParseLine(line);
			}
		}

		public static OperandField? ParseOperandField(char c)
		{
			switch (c)
			{
				case '-': return null;
				case 'r': return OperandField.ModReg;
				case 'm': return OperandField.BaseReg;
				case 'x': return OperandField.IndexReg;
				case 'i': return OperandField.Immediate;
				case 'j': return OperandField.Immediate2;
				case 'v': return OperandField.NonDestructiveReg;
				case 's': return OperandField.IS4;
				default: throw new ArgumentOutOfRangeException(nameof(c));
			}
		}

		public static bool IsIgnoredLine(string line)
		{
			Contract.Requires(line != null);
			// Blank or with comment
			return Regex.IsMatch(line, @"\A\s*(;.*)?\Z", RegexOptions.CultureInvariant);
		}

		public static NasmInsnsEntry ParseLine(string line)
		{
			Contract.Requires(line != null);

			var columnMatches = instructionLineColumnRegex.Matches(line);
			if (columnMatches.Count != 4) throw new FormatException();
			
			var entryBuilder = new NasmInsnsEntry.Builder();

			// Mnemonic
			var mnemonicColumn = columnMatches[0].Value;
			if (!Regex.IsMatch(mnemonicColumn, @"\A[A-Z_0-9]+(cc)?\Z", RegexOptions.CultureInvariant))
				throw new FormatException("Invalid mnemonic column format.");
			entryBuilder.Mnemonic = mnemonicColumn;

			// Encoding
			var codeStringColumn = columnMatches[2].Value;
			var operandFieldsString = string.Empty;
			if (codeStringColumn != "ignore")
			{
				var codeStringMatch = codeStringColumnRegex.Match(codeStringColumn);
				if (!codeStringMatch.Success) throw new FormatException("Invalid code string column format.");

				operandFieldsString = codeStringMatch.Groups["operand_fields"].Value;
				var evexTupleTypesString = codeStringMatch.Groups["evex_tuple_type"].Value;
				var encodingString = codeStringMatch.Groups["encoding"].Value;

				ParseCodeString(entryBuilder, encodingString);

				if (evexTupleTypesString.Length > 0)
				{
					entryBuilder.EVexTupleType = (NasmEVexTupleType)Enum.Parse(
						typeof(NasmEVexTupleType), evexTupleTypesString, ignoreCase: true);
				}
			}

			// Operands
			var operandsColumn = columnMatches[1].Value;
			ParseOperands(entryBuilder, operandFieldsString, operandsColumn);

			// Flags
			var flagsColumn = columnMatches[3].Value;
			ParseFlags(entryBuilder, flagsColumn);
			
			return entryBuilder.Build(reuse: false);
		}

		private static void ParseCodeString(NasmInsnsEntry.Builder entryBuilder, string str)
		{
			var tokens = str.Split(' ');
			int tokenIndex = 0;

			bool hasVex = false;
			while (tokenIndex < tokens.Length)
			{
				var token = tokens[tokenIndex++];

				var tokenType = NasmEncodingToken.TryParseType(token);
				if (tokenType != NasmEncodingTokenType.None)
				{
					entryBuilder.EncodingTokens.Add(tokenType);
					continue;
				}

				byte @byte;
				if (Regex.IsMatch(token, @"\A[0-9a-f]{2}(\+[rc])?\Z")
					&& byte.TryParse(token.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out @byte))
				{
					var type = NasmEncodingTokenType.Byte;
					if (token.Length == 4)
					{
						type = token[token.Length - 1] == 'r'
							? NasmEncodingTokenType.Byte_PlusRegister
							: NasmEncodingTokenType.Byte_PlusConditionCode;
					}
					entryBuilder.EncodingTokens.Add(new NasmEncodingToken(type, @byte));
					continue;
				}

				if (Regex.IsMatch(token, @"\A/[0-7]\Z"))
				{
					entryBuilder.EncodingTokens.Add(new NasmEncodingToken(NasmEncodingTokenType.ModRM_FixedReg, (byte)(token[1] - '0')));
					continue;
				}

				if (Regex.IsMatch(token, @"\A(vex|xop|evex)\."))
				{
					Contract.Assert(!hasVex);
					ParseVex(entryBuilder, token);
					hasVex = true;
					continue;
				}

				throw new FormatException("Unexpected NASM encoding token '{0}'".FormatInvariant(token));
			}
		}

		#region ParseVex
		private static void ParseVex(NasmInsnsEntry.Builder entryBuilder, string str)
		{
			var tokens = str.ToLowerInvariant().Split('.');
			int tokenIndex = 0;

			VexOpcodeEncoding encoding = 0;
			switch (tokens[tokenIndex++])
			{
				case "vex": encoding |= VexOpcodeEncoding.Type_Vex; break;
				case "xop": encoding |= VexOpcodeEncoding.Type_Xop; break;
				case "evex": encoding |= VexOpcodeEncoding.Type_EVex; break;
				default: throw new FormatException();
			}

			if (tokens[tokenIndex][0] == 'm')
			{
				// AMD-Style
				// xop.m8.w0.nds.l0.p0
				// vex.m3.w0.nds.l0.p1
				ParseVex_Map(ref encoding, tokens, ref tokenIndex);
				ParseVex_RexW(ref encoding, tokens, ref tokenIndex);
				ParseVex_Vvvv(ref encoding, tokens, ref tokenIndex);
				ParseVex_VectorLength(ref encoding, tokens, ref tokenIndex);
				ParseVex_SimdPrefix_AmdStyle(ref encoding, tokens, ref tokenIndex);
			}
			else
			{
				// Intel-Style
				// vex.nds.256.66.0f3a.w0
				// evex.nds.512.66.0f3a.w0
				ParseVex_Vvvv(ref encoding, tokens, ref tokenIndex);
				ParseVex_VectorLength(ref encoding, tokens, ref tokenIndex);
				ParseVex_SimdPrefix_IntelStyle(ref encoding, tokens, ref tokenIndex);
				ParseVex_Map(ref encoding, tokens, ref tokenIndex);
				ParseVex_RexW(ref encoding, tokens, ref tokenIndex);
			}

			entryBuilder.EncodingTokens.Add(NasmEncodingTokenType.Vex);
			entryBuilder.VexEncoding = encoding;
		}

		private static void ParseVex_Vvvv(ref VexOpcodeEncoding encoding, string[] tokens, ref int tokenIndex)
		{
			switch (tokens[tokenIndex])
			{
				case "nds": encoding |= VexOpcodeEncoding.NonDestructiveReg_Source; break;
				case "ndd": encoding |= VexOpcodeEncoding.NonDestructiveReg_Dest; break;
				case "dds": encoding |= VexOpcodeEncoding.NonDestructiveReg_SecondSource; break;
				default: encoding |= VexOpcodeEncoding.NonDestructiveReg_Invalid; return;
			}

			++tokenIndex;
		}

		private static void ParseVex_VectorLength(ref VexOpcodeEncoding encoding, string[] tokens, ref int tokenIndex)
		{
			switch (tokens[tokenIndex])
			{
				case "lz": case "l0": case "128": encoding |= VexOpcodeEncoding.VectorLength_0; break;
				case "l1": case "256": encoding |= VexOpcodeEncoding.VectorLength_1; break;
				case "512": encoding |= VexOpcodeEncoding.VectorLength_2; break;
				case "lig": encoding |= VexOpcodeEncoding.VectorLength_Ignored; break;
				default: encoding |= VexOpcodeEncoding.VectorLength_Ignored; return;
			}

			++tokenIndex;
		}

		private static void ParseVex_SimdPrefix_IntelStyle(ref VexOpcodeEncoding encoding, string[] tokens, ref int tokenIndex)
		{
			switch (tokens[tokenIndex])
			{
				case "66": encoding |= VexOpcodeEncoding.SimdPrefix_66; break;
				case "f2": encoding |= VexOpcodeEncoding.SimdPrefix_F2; break;
				case "f3": encoding |= VexOpcodeEncoding.SimdPrefix_F3; break;
				default: encoding |= VexOpcodeEncoding.SimdPrefix_None; return;
			}

			++tokenIndex;
		}

		private static void ParseVex_SimdPrefix_AmdStyle(ref VexOpcodeEncoding encoding, string[] tokens, ref int tokenIndex)
		{
			switch (tokens[tokenIndex])
			{
				case "p0": encoding |= VexOpcodeEncoding.SimdPrefix_None; break;
				case "p1": encoding |= VexOpcodeEncoding.SimdPrefix_66; break;
				default: encoding |= VexOpcodeEncoding.SimdPrefix_None; return;
			}

			++tokenIndex;
		}

		private static void ParseVex_Map(ref VexOpcodeEncoding encoding, string[] tokens, ref int tokenIndex)
		{
			switch (tokens[tokenIndex++]) // Mandatory
			{
				case "0f": encoding |= VexOpcodeEncoding.Map_0F; break;
				case "0f38": encoding |= VexOpcodeEncoding.Map_0F38; break;
				case "m3": case "0f3a": encoding |= VexOpcodeEncoding.Map_0F3A; break;
				case "m8": encoding |= VexOpcodeEncoding.Map_Xop8; break;
				case "m9": encoding |= VexOpcodeEncoding.Map_Xop9; break;
				case "m10": encoding |= VexOpcodeEncoding.Map_Xop10; break;
				default: throw new FormatException();
			}
		}

		private static void ParseVex_RexW(ref VexOpcodeEncoding encoding, string[] tokens, ref int tokenIndex)
		{
			if (tokenIndex == tokens.Length) return;
			switch (tokens[tokenIndex++])
			{
				case "w0": encoding |= VexOpcodeEncoding.RexW_0; break;
				case "w1": encoding |= VexOpcodeEncoding.RexW_1; break;
				case "wig": encoding |= VexOpcodeEncoding.RexW_Ignored; break;
				default: encoding |= VexOpcodeEncoding.RexW_Ignored; return;
			}
		}
		#endregion

		private static void ParseOperands(NasmInsnsEntry.Builder entryBuilder, string fieldsString, string valuesString)
		{
			if (valuesString == "void" || valuesString == "ignore")
			{
				Contract.Assert(fieldsString.Length == 0);
				return;
			}

			if (fieldsString.Length == 0)
			{
				// This only happens for pseudo-instructions
				return;
			}

			valuesString = valuesString.Replace("*", string.Empty); // '*' is for "relaxed", but it's not clear what this encodes
			var values = Regex.Split(valuesString, "[,:]");
			
			if (fieldsString == "r+mi")
			{
				// Hack around the IMUL special case
				fieldsString = "rmi";
				values = new[] { values[0], values[0].Replace("reg", "rm"), values[1] };
			}

			Contract.Assert(values.Length == fieldsString.Length);

			for (int i = 0; i < values.Length; ++i)
			{
				var field = ParseOperandField(fieldsString[i]);

				var valueComponents = values[i].Split('|');
				var typeString = valueComponents[0];
				var type = (NasmOperandType)Enum.Parse(typeof(NasmOperandType), valueComponents[0], ignoreCase: true);
				entryBuilder.Operands.Add(new NasmOperand(field, type));
				// TODO: Parse NASM operand flags (after the '|')
				// TODO: Support star'ed types like "xmmreg*"
			}
		}

		private static void ParseFlags(NasmInsnsEntry.Builder entryBuilder, string str)
		{
			if (str == "ignore") return;
			foreach (var flagStr in str.Split(','))
			{
				var enumerantName = char.IsDigit(flagStr[0]) ? '_' + flagStr : flagStr;
				var flag = (NasmInstructionFlag)Enum.Parse(typeof(NasmInstructionFlag), enumerantName, ignoreCase: true);
				entryBuilder.Flags.Add(flag);
			}
		}
    }
}
