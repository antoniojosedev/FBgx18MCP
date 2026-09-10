using System;
using System.Reflection;

namespace GxMcp.Worker.Compatibility
{
    /// <summary>
    /// Result of invoking an SDK member whose presence varies between GeneXus majors.
    /// Available means the member exists; Succeeded means it also returned without throwing.
    /// </summary>
    internal sealed class OptionalSdkInvocation
    {
        internal OptionalSdkInvocation(bool available, bool succeeded, object value, string error)
        {
            Available = available;
            Succeeded = succeeded;
            Value = value;
            Error = error;
        }

        internal bool Available { get; private set; }
        internal bool Succeeded { get; private set; }
        internal object Value { get; private set; }
        internal string Error { get; private set; }
    }

    /// <summary>
    /// Small reflection boundary for optional Artech SDK members. Keeping this boundary
    /// generic lets future version adapters reuse it without adding compile-time references
    /// to methods that are absent from older SDKs.
    /// </summary>
    internal static class OptionalSdkInvoker
    {
        private const BindingFlags ConstructorFlags =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private const BindingFlags MethodFlags =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        internal static OptionalSdkInvocation InvokeNoArgs(object target, string methodName)
        {
            if (target == null)
                return Unavailable("Target is null.");
            if (string.IsNullOrWhiteSpace(methodName))
                return Unavailable("Method name is empty.");

            MethodInfo method = target.GetType().GetMethod(
                methodName,
                MethodFlags,
                binder: null,
                types: Type.EmptyTypes,
                modifiers: null);

            if (method == null)
                return Unavailable("Method is not present.");

            try
            {
                return new OptionalSdkInvocation(
                    available: true,
                    succeeded: true,
                    value: method.Invoke(target, null),
                    error: null);
            }
            catch (TargetInvocationException ex)
            {
                return Failed(ex.InnerException?.Message ?? ex.Message);
            }
            catch (Exception ex)
            {
                return Failed(ex.Message);
            }
        }

        internal static bool TryCreate(
            string fullTypeName,
            object argument,
            out object instance,
            out string error)
        {
            instance = null;
            error = null;

            Type helperType = FindType(fullTypeName);
            if (helperType == null)
            {
                error = "Type is not present.";
                return false;
            }

            ConstructorInfo constructor = null;
            foreach (ConstructorInfo candidate in helperType.GetConstructors(ConstructorFlags))
            {
                ParameterInfo[] parameters = candidate.GetParameters();
                if (parameters.Length == 1
                    && argument != null
                    && parameters[0].ParameterType.IsInstanceOfType(argument))
                {
                    constructor = candidate;
                    break;
                }
            }

            if (constructor == null)
            {
                error = "No compatible single-argument constructor is present.";
                return false;
            }

            try
            {
                instance = constructor.Invoke(new[] { argument });
                return instance != null;
            }
            catch (TargetInvocationException ex)
            {
                error = ex.InnerException?.Message ?? ex.Message;
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static Type FindType(string fullTypeName)
        {
            if (string.IsNullOrWhiteSpace(fullTypeName)) return null;

            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    Type type = assembly.GetType(fullTypeName, throwOnError: false);
                    if (type != null) return type;
                }
                catch { }
            }

            try
            {
                Assembly common = Assembly.Load("Artech.Genexus.Common");
                return common.GetType(fullTypeName, throwOnError: false);
            }
            catch { return null; }
        }

        private static OptionalSdkInvocation Unavailable(string error)
        {
            return new OptionalSdkInvocation(false, false, null, error);
        }

        private static OptionalSdkInvocation Failed(string error)
        {
            return new OptionalSdkInvocation(true, false, null, error);
        }
    }
}
