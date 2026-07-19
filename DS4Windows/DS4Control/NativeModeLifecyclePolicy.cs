/*
DS4Windows
Copyright (C) 2026  DS4Windows contributors

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program.  If not, see <https://www.gnu.org/licenses/>.
*/

namespace DS4Windows
{
    public static class NativeModeLifecyclePolicy
    {
        public static bool IsSessionActive(NativeModeState state,
            bool suppressionActive, bool ownsChildProcess)
        {
            return suppressionActive || ownsChildProcess ||
                state == NativeModeState.Starting ||
                state == NativeModeState.Serving ||
                state == NativeModeState.Attached;
        }

        public static bool RequiresAutomaticCleanup(NativeModeState state,
            bool suppressionActive)
        {
            return suppressionActive &&
                (state == NativeModeState.PadLost ||
                 state == NativeModeState.Faulted);
        }
    }
}
