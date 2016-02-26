﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;

namespace Injection
{
    /// <summary>
    /// Dependency injectionist that can be used for configuring a system of injected service implementations, possibly with decorators,
    /// with caching of instances so that the same instance of each class is used throughout the tree. Should probably not be used for
    /// anything at runtime, is only meant to be used in configuration scenarios.
    /// </summary>
    public class Injectionist
    {
        class Handler
        {
            public Handler()
            {
                Decorators = new List<Resolver>();
            }

            public Resolver PrimaryResolver { get; private set; }

            public List<Resolver> Decorators { get; private set; }

            public void AddDecorator(Resolver resolver)
            {
                Decorators.Insert(0, resolver);
            }

            public void AddPrimary(Resolver resolver)
            {
                PrimaryResolver = resolver;
            }
        }

        readonly Dictionary<Type, Handler> _resolvers = new Dictionary<Type, Handler>();

        /// <summary>
        /// Starts a new resolution context, resolving an instance of the given <typeparamref name="TService"/>
        /// </summary>
        public ResolutionResult<TService> Get<TService>()
        {
            var resolutionContext = new ResolutionContext(_resolvers);
            var instance = resolutionContext.Get<TService>();
            return new ResolutionResult<TService>(instance, resolutionContext.TrackedInstances);
        }

        /// <summary>
        /// Registers a factory method that can provide an instance of <typeparamref name="TService"/>. Optionally,
        /// the supplied <paramref name="description"/> will be used to report more comprehensible errors in case of
        /// conflicting registrations.
        /// </summary>
        public void Register<TService>(Func<IResolutionContext, TService> resolverMethod, string description = null)
        {
            Register(resolverMethod, description: description,  isDecorator: false);
        }

        /// <summary>
        /// Registers a decorator factory method that can provide an instance of <typeparamref name="TService"/> 
        /// (i.e. the resolver is expected to call <see cref="IResolutionContext.Get{TService}"/> where TService
        /// is <typeparamref name="TService"/>. Optionally, the supplied <paramref name="description"/> will be used 
        /// to report more comprehensible errors in case of conflicting registrations.
        /// </summary>
        public void Decorate<TService>(Func<IResolutionContext, TService> resolverMethod, string description = null)
        {
            Register(resolverMethod, description: description, isDecorator: true);
        }

        /// <summary>
        /// Returns whether there exists a registration for the specified <typeparamref name="TService"/>.
        /// </summary>
        public bool Has<TService>(bool primary = true)
        {
            var key = typeof(TService);
            
            if (!_resolvers.ContainsKey(key) ) return false;

            var handler = _resolvers[key];

            if (handler.PrimaryResolver != null) return true;

            if (!primary && handler.Decorators.Any()) return true;

            return false;
        }

        void Register<TService>(Func<IResolutionContext, TService> resolverMethod, bool isDecorator, string description)
        {
            var key = typeof(TService);
            if (!_resolvers.ContainsKey(key))
            {
                _resolvers.Add(key, new Handler());
            }

            var handler = _resolvers[key];

            var resolver = new Resolver<TService>(resolverMethod, description: description, isDecorator: isDecorator);

            if (!isDecorator)
            {
                if (handler.PrimaryResolver != null)
                {
                    throw new InvalidOperationException(string.Format("Attempted to register {0}, but a primary registration already exists: {1}",
                        resolver, handler.PrimaryResolver));
                }
            }

            if (!resolver.IsDecorator)
            {
                handler.AddPrimary(resolver);
            }
            else
            {
                handler.AddDecorator(resolver);
            }
        }

        abstract class Resolver
        {
            readonly bool _isDecorator;

            protected Resolver(bool isDecorator)
            {
                _isDecorator = isDecorator;
            }

            public bool IsDecorator
            {
                get { return _isDecorator; }
            }
        }

        class Resolver<TService> : Resolver
        {
            readonly Func<IResolutionContext, TService> _resolver;
            readonly string _description;

            public Resolver(Func<IResolutionContext, TService> resolver, bool isDecorator, string description)
                : base(isDecorator)
            {
                _resolver = resolver;
                _description = description;
            }

            public TService InvokeResolver(IResolutionContext context)
            {
                return _resolver(context);
            }

            public override string ToString()
            {
                return !string.IsNullOrWhiteSpace(_description)
                    ? string.Format("{0} {1} ({2})", IsDecorator ? "decorator ->" : "primary ->", typeof (TService), _description)
                    : string.Format("{0} {1}", IsDecorator ? "decorator ->" : "primary ->", typeof (TService));
            }
        }

        class ResolutionContext : IResolutionContext
        {
            readonly Dictionary<Type, int> _decoratorDepth = new Dictionary<Type, int>();
            readonly Dictionary<Type, Handler> _resolvers;
            readonly Dictionary<Type, object> _instances = new Dictionary<Type, object>();
            readonly List<object> _resolvedInstances = new List<object>();

            public ResolutionContext(Dictionary<Type, Handler> resolvers)
            {
                _resolvers = resolvers;
            }

            public TService Get<TService>()
            {
                var serviceType = typeof(TService);

                if (_instances.ContainsKey(serviceType))
                {
                    return (TService)_instances[serviceType];
                }

                if (!_resolvers.ContainsKey(serviceType))
                {
                    throw new ResolutionException("Could not find resolver for {0}", serviceType);
                }

                if (!_decoratorDepth.ContainsKey(serviceType))
                {
                    _decoratorDepth[serviceType] = 0;
                }

                var handlerForThisType = _resolvers[serviceType];
                var depth = _decoratorDepth[serviceType]++;

                try
                {
                    var resolver = handlerForThisType
                        .Decorators
                        .Cast<Resolver<TService>>()
                        .Skip(depth)
                        .FirstOrDefault()
                        ?? (Resolver<TService>)handlerForThisType.PrimaryResolver;

                    var instance = resolver.InvokeResolver(this);

                    _instances[serviceType] = instance;

                    if (!_resolvedInstances.Contains(instance))
                    {
                        _resolvedInstances.Add(instance);
                    }

                    return instance;
                }
                catch (Exception exception)
                {
                    throw new ResolutionException(exception, "Could not resolve {0} with decorator depth {1} - registrations: {2}",
                        serviceType, depth, string.Join("; ", handlerForThisType));
                }
                finally
                {
                    _decoratorDepth[serviceType]--;
                }
            }

            public IEnumerable TrackedInstances
            {
                get { return _resolvedInstances.ToList(); }
            }
        }
    }

    /// <summary>
    /// Represents the context of resolving one root service and can be used throughout the tree to fetch something to be injected
    /// </summary>
    public interface IResolutionContext
    {
        /// <summary>
        /// Gets an instance of the specified <typeparamref name="TService"/>.
        /// </summary>
        TService Get<TService>();

        /// <summary>
        /// Gets all instances resolved within this resolution context at this time.
        /// </summary>
        IEnumerable TrackedInstances { get; }
    }

    /// <summary>
    /// Exceptions that is thrown when something goes wrong while working with the injectionist
    /// </summary>
    [Serializable]
    public class ResolutionException : Exception
    {
        /// <summary>
        /// Constructs the exception
        /// </summary>
        public ResolutionException(string message, params object[] objs)
            : base(string.Format(message, objs))
        {
        }

        /// <summary>
        /// Constructs the exception
        /// </summary>
        public ResolutionException(Exception innerException, string message, params object[] objs)
            : base(string.Format(message, objs), innerException)
        {
        }

        /// <summary>
        /// Constructs the exception
        /// </summary>
        public ResolutionException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }
    }

    /// <summary>
    /// Contains a built object instance along with all the objects that were used to build the instance
    /// </summary>
    public class ResolutionResult<TService>
    {
        internal ResolutionResult(TService instance, IEnumerable trackedInstances)
        {
            if (instance == null) throw new ArgumentNullException("instance");
            if (trackedInstances == null) throw new ArgumentNullException("trackedInstances");
            Instance = instance;
            TrackedInstances = trackedInstances;
        }

        /// <summary>
        /// Gets the instance that was built
        /// </summary>
        public TService Instance { get; private set; }

        /// <summary>
        /// Gets all object instances that were used to build <see cref="Instance"/>, including the instance itself
        /// </summary>
        public IEnumerable TrackedInstances { get; private set; }
    }
}
