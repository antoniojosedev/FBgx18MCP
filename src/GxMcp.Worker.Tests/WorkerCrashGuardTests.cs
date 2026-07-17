using System;
using System.Runtime.InteropServices;
using GxMcp.Worker.Helpers;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // issue #35 worker-stability hardening. The end-to-end "a real AccessViolation is caught with
    // legacyCorruptedStateExceptionsPolicy" was proven with a standalone net48 harness (raising a
    // real AV via Marshal.ReadInt32 and catching it); it isn't reproduced in-suite because a real
    // corrupted-state fault would risk the test host. These cover the classification logic that
    // decides between "recover this call + respawn" and "normal error, keep serving".
    public class WorkerCrashGuardTests
    {
        [Fact]
        public void IsCorruptedState_AccessViolation_IsTrue()
        {
            Assert.True(WorkerCrashGuard.IsCorruptedState(new AccessViolationException()));
        }

        [Fact]
        public void IsCorruptedState_SEH_IsTrue()
        {
            Assert.True(WorkerCrashGuard.IsCorruptedState(new SEHException()));
        }

        [Fact]
        public void IsCorruptedState_WrappedAccessViolation_IsTrue()
        {
            var wrapped = new InvalidOperationException("outer", new AccessViolationException());
            Assert.True(WorkerCrashGuard.IsCorruptedState(wrapped));
        }

        [Theory]
        [InlineData(typeof(InvalidOperationException))]
        [InlineData(typeof(ArgumentException))]
        [InlineData(typeof(NullReferenceException))]
        public void IsCorruptedState_OrdinaryExceptions_AreFalse(Type exType)
        {
            var ex = (Exception)Activator.CreateInstance(exType);
            Assert.False(WorkerCrashGuard.IsCorruptedState(ex));
        }

        [Fact]
        public void IsCorruptedState_Null_IsFalse()
        {
            Assert.False(WorkerCrashGuard.IsCorruptedState(null));
        }

        [Fact]
        public void CrashReason_AccessViolation_NamesIt()
        {
            Assert.Equal("AccessViolation", WorkerCrashGuard.CrashReason(new AccessViolationException()));
        }

        [Fact]
        public void CrashReason_OrdinaryException_ReturnsTypeName()
        {
            Assert.Equal("InvalidOperationException", WorkerCrashGuard.CrashReason(new InvalidOperationException()));
        }
    }
}
