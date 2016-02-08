﻿using System;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Asmuth.Debugger
{
	using static NativeMethods;
	using static Kernel32;

	public sealed class ThreadDebugger
	{
		private readonly ProcessDebugger process;
		private readonly CREATE_THREAD_DEBUG_INFO debugInfo;
		private ulong? instructionPointer;

		// Called on worker thread
		internal ThreadDebugger(ProcessDebugger process, CREATE_THREAD_DEBUG_INFO debugInfo)
		{
			Contract.Requires(process != null);
			Contract.Requires(debugInfo.hThread != IntPtr.Zero);
			this.process = process;
			this.debugInfo = debugInfo;
		}
		
		public int ID => unchecked((int)GetThreadId(debugInfo.hThread));
		public ProcessDebugger Process => process;
		public bool IsRunning => !instructionPointer.HasValue;
		public ulong InstructionPointer => instructionPointer.Value;

		public void Continue(bool handled = true)
		{
			Contract.Requires(!IsRunning);
			instructionPointer = null;
			CheckWin32(ContinueDebugEvent(
				unchecked((uint)process.ID),
				unchecked((uint)ID),
				handled ? DBG_CONTINUE : DBG_EXCEPTION_NOT_HANDLED));
		}

		// Called on either thread
		internal void Dispose()
		{
			CloseHandle(debugInfo.hThread);
		}

		// Called on worker thread
		internal void OnBroken(ulong instructionPointer)
		{
			this.instructionPointer = instructionPointer;
		}

		// Called on worker thread
		internal void OnContinued()
		{
			this.instructionPointer = null;
		}
	}
}
