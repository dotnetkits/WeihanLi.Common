﻿using System;
using System.Reflection;

namespace WeihanLi.Common.Aspect
{
    public interface IInvocation
    {
        public MethodInfo ProxyMethod { get; }

        public object ProxyTarget { get; }

        public MethodInfo Method { get; }

        public object Target { get; }

        public object[] Arguments { get; }

        Type[] GenericArguments { get; }

        public object ReturnValue { get; set; }
    }

    public class AspectInvocation : IInvocation
    {
        public MethodInfo ProxyMethod { get; }

        public object ProxyTarget { get; }

        public MethodInfo Method { get; }

        public object Target { get; }

        public object[] Arguments { get; }

        public Type[] GenericArguments { get; }

        public object ReturnValue { get; set; }

        public AspectInvocation(
            MethodInfo proxyMethod,
            MethodInfo methodBase,
            object proxyTarget,
            object target,
            object[] arguments)
        {
            Method = methodBase;
            ProxyTarget = proxyTarget;
            Target = target;
            Arguments = arguments;
            GenericArguments = methodBase?.GetGenericArguments() ?? Type.EmptyTypes;

            if (proxyMethod.ContainsGenericParameters && GenericArguments.Length > 0)
            {
                ProxyMethod = proxyMethod.MakeGenericMethod(GenericArguments);
            }
            else
            {
                ProxyMethod = proxyMethod;
            }
        }
    }
}
