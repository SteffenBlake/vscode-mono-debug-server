/*---------------------------------------------------------------------------------------------
 *  Copyright (c) Microsoft Corporation. All rights reserved.
 *  Licensed under the MIT License. See License.txt in the project root for license information.
 *
 *  Fork (c) 2026 Steffen Blake, Modified for Mono debugger CLI Server.
 *--------------------------------------------------------------------------------------------*/
using System.Collections.Generic;

namespace VsCodeMonoDebugServer.Console
{
	public class Handles<T>
	{
		private const int START_HANDLE = 1000;

		private int _nextHandle;
		private Dictionary<int, T> _handleMap;

		public Handles() {
			_nextHandle = START_HANDLE;
			_handleMap = new Dictionary<int, T>();
		}

		public void Reset()
		{
			_nextHandle = START_HANDLE;
			_handleMap.Clear();
		}

		public int Create(T value)
		{
			var handle = _nextHandle++;
			_handleMap[handle] = value;
			return handle;
		}

		public bool TryGet(int handle, out T value)
		{
			if (_handleMap.TryGetValue(handle, out value)) {
				return true;
			}
			return false;
		}

		public T Get(int handle, T dflt)
		{
			T value;
			if (_handleMap.TryGetValue(handle, out value)) {
				return value;
			}
			return dflt;
		}
	}
}
