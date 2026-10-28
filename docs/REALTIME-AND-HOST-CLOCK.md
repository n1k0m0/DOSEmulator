# Realtime execution and host calendar

Status: 28 October 2026. Development checkpoint; the realtime target is still open.

The target is one emulated second per elapsed host second in every supported hardware profile. The selected CPU frequency, existing four-clock instruction accounting, device deadlines and guest instructions are preserved. The frontend's pacer waits when execution is ahead and stops waiting when it falls behind. Displaying 100 percent or changing the emulated CPU frequency would not establish sufficient execution throughput.

The original V12 audit measured a Windows 95 desktop workload below realtime at both 100 and 133 MHz. Subsequent isolated ARPL fault-transport prototypes remove allocations in tight invalid-instruction loops, but the Windows comparisons have not established a stable improvement. A stateless register-instruction batch prototype is included for complete-state regression testing and is not connected to the product execution loop in this checkpoint. It must pass device-deadline and real-guest comparisons before activation.

The frontend now measures actual virtual time divided by elapsed monotonic host time in complete host-second windows, including pacing waits. The window title shows `SPEED`, and the English `TIME AND SPEED` page controls host calendar synchronization. Native window/input verification remains open.

With synchronization enabled, startup, machine replacement, snapshot restore and resume explicitly supply the current host local calendar to the emulated RTC. Running execution receives a correction once per host second. A paused machine does not execute devices or periodically change its RTC. This implements the user's requested policy: loading and resuming adopt current host time. The low-level hardware and strict snapshot loader never read the host clock themselves; callers can still run deterministic machines without this frontend policy.

The RTC preserves its scheduler-relative calendar origin and subsecond state in snapshots. It supports BCD/binary and 12/24-hour reads. The new calendar validation runs before media branch publication. Guest calendar writes, alarm interrupts and complete historical RTC behavior remain separate limitations.

Windows 95 maintains its own software clock. The own [DOSE Tools clock helper](../guest-tools/windows95/README.txt) reads real emulated CMOS ports and uses Windows time APIs. Actual `/once` runs on the tested OEM/950 guest verify the complete proof file, successful time-setting APIs and calendar differences of 40 and 30 milliseconds; the contract is at most one second because CMOS has whole-second resolution. A successful asynchronous `WM_TIMECHANGE` broadcast is also observed. This does not constitute an installed-background-helper, autostart, native UI or shutdown acceptance.

The new English installer, `C:\Share` creation, startup registration and background timer are source-complete and assemble as an own guest PE. The final English confirmation dialog and installed background behavior still require actual guest verification. File import/export, clipboard sharing and host-folder mapping are not implemented by this clock package.

The accepted common starter and existing private Windows working PCs remain on frozen V12. New builds use their own directories. Snapshot format 1 remains strictly bound to the actual five VM modules; these source changes do not migrate old containers. Product source, own firmware and tests can be committed; licensed media, installed Windows disks, RAM captures and private test evidence remain ignored.
