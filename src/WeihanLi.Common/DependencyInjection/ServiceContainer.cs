﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace WeihanLi.Common.DependencyInjection
{
    public interface IServiceContainer : IScope, IServiceProvider
    {
        void Add(ServiceDefinition item);

        IServiceContainer CreateScope();
    }

    public class ServiceContainer : IServiceContainer
    {
        internal readonly ConcurrentBag<ServiceDefinition> _services;

        private readonly ConcurrentDictionary<ServiceDefinitionKey, object> _singletonInstances;

        private readonly ConcurrentDictionary<ServiceDefinitionKey, object> _scopedInstances;
        private ConcurrentBag<object> _transientDisposables = new ConcurrentBag<object>();

        private class ServiceDefinitionKey
        {
            public Type ServiceType { get; }

            public Type ImplementType { get; }

            public ServiceDefinitionKey(Type serviceType, ServiceDefinition definition)
            {
                ServiceType = serviceType;
                ImplementType = definition.GetImplementType();
            }
        }

        private readonly bool _isRootScope;

        public ServiceContainer()
        {
            _isRootScope = true;
            _singletonInstances = new ConcurrentDictionary<ServiceDefinitionKey, object>();
            _services = new ConcurrentBag<ServiceDefinition>();
        }

        private ServiceContainer(ServiceContainer serviceContainer)
        {
            _isRootScope = false;
            _singletonInstances = serviceContainer._singletonInstances;
            _services = serviceContainer._services;
            _scopedInstances = new ConcurrentDictionary<ServiceDefinitionKey, object>();
        }

        public void Add(ServiceDefinition item)
        {
            if (_disposed)
            {
                return;
            }
            _services.Add(item);
        }

        public IServiceContainer CreateScope()
        {
            return new ServiceContainer(this);
        }

        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            if (_isRootScope)
            {
                lock (_singletonInstances)
                {
                    if (_disposed)
                    {
                        return;
                    }

                    _disposed = true;
                    foreach (var instance in _singletonInstances.Values)
                    {
                        (instance as IDisposable)?.Dispose();
                    }

                    foreach (var o in _transientDisposables)
                    {
                        (o as IDisposable)?.Dispose();
                    }

                    _singletonInstances.Clear();
                    _transientDisposables = null;
                }
            }
            else
            {
                lock (_scopedInstances)
                {
                    if (_disposed)
                    {
                        return;
                    }

                    _disposed = true;
                    foreach (var instance in _scopedInstances.Values)
                    {
                        (instance as IDisposable)?.Dispose();
                    }

                    foreach (var o in _transientDisposables)
                    {
                        (o as IDisposable)?.Dispose();
                    }

                    _scopedInstances.Clear();
                    _transientDisposables = null;
                }
            }
        }

        private object GetServiceInstance(Type serviceType, ServiceDefinition serviceDefinition)
        {
            if (serviceDefinition.ImplementationInstance != null)
                return serviceDefinition.ImplementationInstance;

            if (serviceDefinition.ImplementationFactory != null)
                return serviceDefinition.ImplementationFactory.Invoke(this);

            var implementType = (serviceDefinition.ImplementType ?? serviceType);

            if (implementType.IsInterface || implementType.IsAbstract)
            {
                throw new InvalidOperationException($"invalid service registered, serviceType: {serviceType.FullName}, implementType: {serviceDefinition.ImplementType}");
            }

            if (implementType.IsGenericType)
            {
                implementType = implementType.MakeGenericType(serviceType.GetGenericArguments());
            }

            var ctorInfos = implementType.GetConstructors(BindingFlags.Instance | BindingFlags.Public);
            if (ctorInfos.Length == 0)
            {
                throw new InvalidOperationException($"service {serviceType.FullName} does not have any public constructors");
            }

            ConstructorInfo ctor;
            if (ctorInfos.Length == 1)
            {
                ctor = ctorInfos[0];
            }
            else
            {
                // TODO: try find best ctor
                ctor = ctorInfos
                    .OrderBy(_ => _.GetParameters().Length)
                    .First();
            }

            var parameters = ctor.GetParameters();
            if (parameters.Length == 0)
            {
                // TODO: cache New Func
                return Expression.Lambda<Func<object>>(Expression.New(ctor)).Compile().Invoke();
            }
            else
            {
                var ctorParams = new object[parameters.Length];
                for (var index = 0; index < parameters.Length; index++)
                {
                    var parameter = parameters[index];
                    var param = GetService(parameter.ParameterType);
                    if (param == null && parameter.HasDefaultValue)
                    {
                        param = parameter.DefaultValue;
                    }

                    ctorParams[index] = param;
                }
                return Expression.Lambda<Func<object>>(Expression.New(ctor, ctorParams.Select(Expression.Constant))).Compile().Invoke();
            }
        }

        public object GetService(Type serviceType)
        {
            if (_disposed)
            {
                throw new InvalidOperationException($"can not get scope service from a disposed scope, serviceType: {serviceType.FullName}");
            }

            var serviceDefinition = _services.LastOrDefault(_ => _.ServiceType == serviceType);
            if (null == serviceDefinition)
            {
                if (serviceType.IsGenericType)
                {
                    var genericType = serviceType.GetGenericTypeDefinition();
                    serviceDefinition = _services.LastOrDefault(_ => _.ServiceType == genericType);
                    if (null == serviceDefinition)
                    {
                        var innerServiceType = serviceType.GetGenericArguments().First();
                        if (typeof(IEnumerable<>).MakeGenericType(innerServiceType)
                            .IsAssignableFrom(serviceType))
                        {
                            if (innerServiceType.IsGenericType)
                            {
                                innerServiceType = innerServiceType.GetGenericTypeDefinition();
                            }
                            //
                            var list = new List<object>(4);
                            foreach (var def in _services.Where(_ => _.ServiceType == innerServiceType))
                            {
                                object svc;
                                if (def.ServiceLifetime == ServiceLifetime.Singleton)
                                {
                                    svc = _singletonInstances.GetOrAdd(new ServiceDefinitionKey(innerServiceType, def), (t) => GetServiceInstance(innerServiceType, def));
                                }
                                else if (def.ServiceLifetime == ServiceLifetime.Scoped)
                                {
                                    svc = _scopedInstances.GetOrAdd(new ServiceDefinitionKey(innerServiceType, def), (t) => GetServiceInstance(innerServiceType, def));
                                }
                                else
                                {
                                    svc = GetServiceInstance(innerServiceType, def);
                                    if (svc is IDisposable)
                                    {
                                        _transientDisposables.Add(svc);
                                    }
                                }
                                if (null != svc)
                                {
                                    list.Add(svc);
                                }
                            }
                            return list;
                        }

                        return null;
                    }
                }
                else
                {
                    return null;
                }
            }

            if (_isRootScope && serviceDefinition.ServiceLifetime == ServiceLifetime.Scoped)
            {
                throw new InvalidOperationException($"can not get scope service from the root scope, serviceType: {serviceType.FullName}");
            }

            if (serviceDefinition.ServiceLifetime == ServiceLifetime.Singleton)
            {
                var svc = _singletonInstances.GetOrAdd(new ServiceDefinitionKey(serviceType, serviceDefinition), (t) => GetServiceInstance(t.ServiceType, serviceDefinition));
                return svc;
            }
            else if (serviceDefinition.ServiceLifetime == ServiceLifetime.Scoped)
            {
                var svc = _scopedInstances.GetOrAdd(new ServiceDefinitionKey(serviceType, serviceDefinition), (t) => GetServiceInstance(t.ServiceType, serviceDefinition));
                return svc;
            }
            else
            {
                var svc = GetServiceInstance(serviceType, serviceDefinition);
                if (svc is IDisposable)
                {
                    _transientDisposables.Add(svc);
                }
                return svc;
            }
        }
    }
}
