﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Net.Http;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace RestEase
{
    internal struct IndexedParameter
    {
        public readonly int Index;
        public readonly ParameterInfo Parameter;

        public IndexedParameter(int index, ParameterInfo parameter)
        {
            this.Index = index;
            this.Parameter = parameter;
        }
    }

    internal struct IndexedParameter<T> where T : Attribute
    { 
        public readonly int Index;
        public readonly ParameterInfo Parameter;
        public readonly T Attribute;

        public IndexedParameter(int index, ParameterInfo parameter, T attribute)
	    {
            this.Index = index;
            this.Parameter = parameter;
            this.Attribute = attribute;
	    }
    }

    internal class ParameterGrouping
    {
        public List<IndexedParameter<PathParamAttribute>> PathParameters { get; private set; }
        public List<IndexedParameter<QueryParamAttribute>> QueryParameters { get; private set; }
        public List<IndexedParameter<HeaderAttribute>> HeaderParameters { get; private set; }
        public List<IndexedParameter> PlainParameters { get; private set; }
        public IndexedParameter<BodyAttribute>? Body { get; private set; }
        public IndexedParameter? CancellationToken { get; private set; }

        public ParameterGrouping(IEnumerable<ParameterInfo> parameters, string methodName)
        {
            this.PathParameters = new List<IndexedParameter<PathParamAttribute>>();
            this.QueryParameters = new List<IndexedParameter<QueryParamAttribute>>();
            this.HeaderParameters = new List<IndexedParameter<HeaderAttribute>>();
            this.PlainParameters = new List<IndexedParameter>();

            // Index 0 is 'this'
            var indexedParameters = parameters.Select((x, i) => new { Index = i + 1, Parameter = x });
            foreach (var parameter in indexedParameters)
            {
                if (parameter.Parameter.ParameterType == typeof(CancellationToken))
                {
                    if (this.CancellationToken.HasValue)
                        throw new RestEaseImplementationCreationException(String.Format("Found more than one parameter of type CancellationToken for method {0}", methodName));
                    this.CancellationToken = new IndexedParameter(parameter.Index, parameter.Parameter);
                    continue;
                }

                var bodyAttribute = parameter.Parameter.GetCustomAttribute<BodyAttribute>();
                if (bodyAttribute != null)
                {
                    if (this.Body.HasValue)
                        throw new RestEaseImplementationCreationException(String.Format("Found more than one parameter with a [Body] attribute for method {0}", methodName));
                    this.Body = new IndexedParameter<BodyAttribute>(parameter.Index, parameter.Parameter, bodyAttribute);
                    continue;
                }

                var queryParamAttribute = parameter.Parameter.GetCustomAttribute<QueryParamAttribute>();
                if (queryParamAttribute != null)
                {
                    this.QueryParameters.Add(new IndexedParameter<QueryParamAttribute>(parameter.Index, parameter.Parameter, queryParamAttribute));
                    continue;
                }

                var pathParamAttribute = parameter.Parameter.GetCustomAttribute<PathParamAttribute>();
                if (pathParamAttribute != null)
                {
                    this.PathParameters.Add(new IndexedParameter<PathParamAttribute>(parameter.Index, parameter.Parameter, pathParamAttribute));
                    continue;
                }

                var headerAttribute = parameter.Parameter.GetCustomAttribute<HeaderAttribute>();
                if (headerAttribute != null)
                {
                    this.HeaderParameters.Add(new IndexedParameter<HeaderAttribute>(parameter.Index, parameter.Parameter, headerAttribute));
                    continue;
                }

                // Anything left? It's a query parameter
                this.PlainParameters.Add(new IndexedParameter(parameter.Index, parameter.Parameter));
            }
        }
    }

    public class ImplementationBuilder
    {
        private static readonly Regex pathParamMatch = new Regex(@"\{(.+?)\}");

        private static readonly string factoryAssemblyName = "RestEaseAutoGeneratedAssembly";
        private static readonly string moduleBuilderName = "RestEaseAutoGeneratedModule";

        private static readonly MethodInfo requestVoidAsyncMethod = typeof(IRequester).GetMethod("RequestVoidAsync");
        private static readonly MethodInfo requestAsyncMethod = typeof(IRequester).GetMethod("RequestAsync");
        private static readonly MethodInfo requestWithResponseMessageAsyncMethod = typeof(IRequester).GetMethod("RequestWithResponseMessageAsync");
        private static readonly MethodInfo requestWithResponseAsyncMethod = typeof(IRequester).GetMethod("RequestWithResponseAsync");
        private static readonly ConstructorInfo requestInfoCtor = typeof(RequestInfo).GetConstructor(new[] { typeof(HttpMethod), typeof(string), typeof(CancellationToken) });
        private static readonly MethodInfo cancellationTokenNoneGetter = typeof(CancellationToken).GetProperty("None").GetMethod;
        private static readonly MethodInfo addQueryParameterMethod = typeof(RequestInfo).GetMethod("AddQueryParameter");
        private static readonly MethodInfo addPathParameterMethod = typeof(RequestInfo).GetMethod("AddPathParameter");
        private static readonly MethodInfo addClassHeaderMethod = typeof(RequestInfo).GetMethod("AddClassHeader");
        private static readonly MethodInfo addMethodHeaderMethod = typeof(RequestInfo).GetMethod("AddMethodHeader");
        private static readonly MethodInfo addHeaderParameterMethod = typeof(RequestInfo).GetMethod("AddHeaderParameter");
        private static readonly MethodInfo setBodyParameterInfoMethod = typeof(RequestInfo).GetMethod("SetBodyParameterInfo");
        private static readonly MethodInfo toStringMethod = typeof(Object).GetMethod("ToString");

        private static readonly Dictionary<HttpMethod, PropertyInfo> httpMethodProperties = new Dictionary<HttpMethod, PropertyInfo>()
        {
            { HttpMethod.Delete, typeof(HttpMethod).GetProperty("Delete") },
            { HttpMethod.Get, typeof(HttpMethod).GetProperty("Get") },
            { HttpMethod.Head, typeof(HttpMethod).GetProperty("Head") },
            { HttpMethod.Options, typeof(HttpMethod).GetProperty("Options") },
            { HttpMethod.Post, typeof(HttpMethod).GetProperty("Post") },
            { HttpMethod.Put, typeof(HttpMethod).GetProperty("Put") },
            { HttpMethod.Trace, typeof(HttpMethod).GetProperty("Trace") }
        };

        private readonly ModuleBuilder moduleBuilder;
        private readonly ConcurrentDictionary<Type, Func<IRequester, object>> creatorCache = new ConcurrentDictionary<Type, Func<IRequester, object>>();

        public ImplementationBuilder()
        {
            var assemblyName = new AssemblyName(factoryAssemblyName);
            var assemblyBuilder = AppDomain.CurrentDomain.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.Run);
            var moduleBuilder = assemblyBuilder.DefineDynamicModule(moduleBuilderName);
            this.moduleBuilder = moduleBuilder;
        }

        public T CreateImplementation<T>(IRequester requester)
        {
            if (requester == null)
                throw new ArgumentNullException("requester");

            var creator = this.creatorCache.GetOrAdd(typeof(T), key =>
            {
                var implementationType = this.BuildImplementationImpl(key);
                return this.BuildCreator(implementationType);
            });

            T implementation = (T)creator(requester);

            return implementation;
        }

        private Func<IRequester, object> BuildCreator(Type implementationType)
        {
            var requesterParam = Expression.Parameter(typeof(IRequester));
            var ctor = Expression.New(implementationType.GetConstructor(new[] { typeof(IRequester) }), requesterParam);
            return Expression.Lambda<Func<IRequester, object>>(ctor, requesterParam).Compile();
        }

        private Type BuildImplementationImpl(Type interfaceType)
        {
            if (!interfaceType.IsInterface)
                throw new ArgumentException(String.Format("Type {0} is not an interface", interfaceType.Name));

            var typeBuilder = this.moduleBuilder.DefineType(String.Format("RestEase.AutoGenerated.{0}", interfaceType.FullName), TypeAttributes.Public);
            typeBuilder.AddInterfaceImplementation(interfaceType);

            // Define a field which holds a reference to the IRequester
            var requesterField = typeBuilder.DefineField("requester", typeof(IRequester), FieldAttributes.Private);

            // Add a constructor which takes the IRequester and assigns it to the field
            // public Name(IRequester requester)
            // {
            //     this.requester = requester;
            // }
            var ctorBuilder = typeBuilder.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, new[] { typeof(IRequester) });
            var ctorIlGenerator = ctorBuilder.GetILGenerator();
            // Load 'this' and the requester onto the stack
            ctorIlGenerator.Emit(OpCodes.Ldarg_0);
            ctorIlGenerator.Emit(OpCodes.Ldarg_1);
            // Store the requester into this.requester
            ctorIlGenerator.Emit(OpCodes.Stfld, requesterField);
            ctorIlGenerator.Emit(OpCodes.Ret);

            var classHeaders = interfaceType.GetCustomAttributes<HeaderAttribute>();

            foreach (var methodInfo in interfaceType.GetMethods())
            {
                var requestAttribute = methodInfo.GetCustomAttribute<RequestAttribute>();
                if (requestAttribute == null)
                    throw new RestEaseImplementationCreationException(String.Format("Method {0} does not have a suitable attribute on it", methodInfo.Name));

                var parameters = methodInfo.GetParameters();
                var parameterGrouping = new ParameterGrouping(parameters, methodInfo.Name);

                this.ValidatePathParams(requestAttribute.Path, parameterGrouping.PathParameters.Select(x => x.Attribute.Name ?? x.Parameter.Name));

                var methodBuilder = typeBuilder.DefineMethod(methodInfo.Name, MethodAttributes.Public | MethodAttributes.Virtual, methodInfo.ReturnType, parameters.Select(x => x.ParameterType).ToArray());
                var methodIlGenerator = methodBuilder.GetILGenerator();

                // Load 'this' onto the stack
                // Stack: [this]
                methodIlGenerator.Emit(OpCodes.Ldarg_0);
                // Load 'this.requester' onto the stack
                // Stack: [this.requester]
                methodIlGenerator.Emit(OpCodes.Ldfld, requesterField);

                // Start loading the ctor params for RequestInfo onto the stack
                // 1. HttpMethod
                // Stack: [this.requester, HttpMethod]
                methodIlGenerator.Emit(OpCodes.Call, httpMethodProperties[requestAttribute.Method].GetMethod);
                // 2. The Path
                // Stack: [this.requester, HttpMethod, path]
                methodIlGenerator.Emit(OpCodes.Ldstr, requestAttribute.Path);
                // 3. The CancellationToken
                // Stack: [this.requester, HttpMethod, path, cancellationToken]
                if (parameterGrouping.CancellationToken != null)
                    methodIlGenerator.Emit(OpCodes.Ldarg, (short)parameterGrouping.CancellationToken.Value.Index);
                else
                    methodIlGenerator.Emit(OpCodes.Call, cancellationTokenNoneGetter);

                // Ctor the RequestInfo
                // Stack: [this.requester, requestInfo]
                methodIlGenerator.Emit(OpCodes.Newobj, requestInfoCtor);

                // If there are any class headers, add them
                foreach (var classHeader in classHeaders)
                {
                    this.AddClassHeader(methodIlGenerator, classHeader);
                }

                // If there are any method headers, add them
                var methodHeaders = methodInfo.GetCustomAttributes<HeaderAttribute>();
                foreach (var methodHeader in methodHeaders)
                {
                    this.AddMethodHeader(methodIlGenerator, methodHeader);
                }

                // If there's a body, add it
                if (parameterGrouping.Body != null)
                {
                    var body = parameterGrouping.Body.Value;
                    this.AddBody(methodIlGenerator, body.Attribute.SerializationMethod, body.Parameter.ParameterType, (short)body.Index);
                }

                foreach (var queryParameter in parameterGrouping.QueryParameters)
                {
                    this.AddParam(methodIlGenerator, queryParameter.Attribute.Name ?? queryParameter.Parameter.Name, (short)queryParameter.Index, addQueryParameterMethod);
                }

                foreach (var plainParameter in parameterGrouping.PlainParameters)
                {
                    this.AddParam(methodIlGenerator, plainParameter.Parameter.Name, (short)plainParameter.Index, addQueryParameterMethod);
                }

                foreach (var pathParameter in parameterGrouping.PathParameters)
                {
                    this.AddParam(methodIlGenerator, pathParameter.Attribute.Name ?? pathParameter.Parameter.Name, (short)pathParameter.Index, addPathParameterMethod);
                }

                foreach (var headerParameter in parameterGrouping.HeaderParameters)
                {
                    this.AddParam(methodIlGenerator, headerParameter.Attribute.Value, (short)headerParameter.Index, addHeaderParameterMethod);
                }

                // Call the appropriate RequestVoidAsync/RequestAsync method, depending on whether or not we have a return type
                if (methodInfo.ReturnType == typeof(Task))
                {
                    // Stack: [Task]
                    methodIlGenerator.Emit(OpCodes.Callvirt, requestVoidAsyncMethod);
                }
                else if (methodInfo.ReturnType.IsGenericType && methodInfo.ReturnType.GetGenericTypeDefinition() == typeof(Task<>))
                {
                    var typeOfT = methodInfo.ReturnType.GetGenericArguments()[0];
                    // Now, is it a Task<HttpResponseMessage>, a Task<Response<T>> or a Task<T>?
                    if (typeOfT == typeof(HttpResponseMessage))
                    {
                        // Stack: [Task<HttpResponseMessage>]
                        methodIlGenerator.Emit(OpCodes.Callvirt, requestWithResponseMessageAsyncMethod);
                    }
                    else if (typeOfT.IsGenericType && typeOfT.GetGenericTypeDefinition() == typeof(Response<>))
                    {
                        // Stack: [Task<Response<T>>]
                        var typedRequestWithResponseAsyncMethod = requestWithResponseAsyncMethod.MakeGenericMethod(typeOfT.GetGenericArguments()[0]);
                        methodIlGenerator.Emit(OpCodes.Callvirt, typedRequestWithResponseAsyncMethod);
                    }
                    else
                    {
                        // Stack: [Task<T>]
                        var typedRequestAsyncMethod = requestAsyncMethod.MakeGenericMethod(typeOfT);
                        methodIlGenerator.Emit(OpCodes.Callvirt, typedRequestAsyncMethod);
                    }
                }
                else
                {
                    throw new RestEaseImplementationCreationException(String.Format("Method {0} has a return type that is not Task<T> or Task", methodInfo.Name));
                }

                // Finally, return
                methodIlGenerator.Emit(OpCodes.Ret);

                typeBuilder.DefineMethodOverride(methodBuilder, methodInfo);
            }

            Type constructedType;
            try
            {
                constructedType = typeBuilder.CreateType();
            }
            catch (TypeLoadException e)
            {
                var msg = String.Format("Unable to create implementation for interface {0}. Ensure that the interface is public", interfaceType.FullName);
                throw new RestEaseImplementationCreationException(msg, e);
            }

            return constructedType;
        }

        private void AddBody(ILGenerator methodIlGenerator, BodySerializationMethod serializationMethod, Type parameterType, short parameterIndex)
        {
            // Equivalent C#:
            // requestInfo.SetBodyParameterInfo(serializationMethod, value)

            // Stack: [..., requestInfo, requestInfo]
            methodIlGenerator.Emit(OpCodes.Dup);
            // Stack: [..., requestInfo, requestInfo, serializationMethod]
            methodIlGenerator.Emit(OpCodes.Ldc_I4, (int)serializationMethod);
            // Stack: [..., requestInfo, requestInfo, serializationMethod, parameter]
            methodIlGenerator.Emit(OpCodes.Ldarg, parameterIndex);
            // If the parameter's a value type, we need to box it
            if (parameterType.IsValueType)
                methodIlGenerator.Emit(OpCodes.Box, parameterType);
            // Stack: [..., requestInfo]
            methodIlGenerator.Emit(OpCodes.Callvirt, setBodyParameterInfoMethod);
        }

        private void AddClassHeader(ILGenerator methodIlGenerator, HeaderAttribute header)
        {
            // Equivalent C#:
            // requestInfo.AddClassHeader("value");

            // Stack: [..., requestInfo, requestInfo]
            methodIlGenerator.Emit(OpCodes.Dup);
            // Stack: [..., requestInfo, requestInfo, "value"]
            methodIlGenerator.Emit(OpCodes.Ldstr, header.Value);
            // Stack: [..., requestInfo]
            methodIlGenerator.Emit(OpCodes.Callvirt, addClassHeaderMethod);
        }

        private void AddMethodHeader(ILGenerator methodIlGenerator, HeaderAttribute header)
        {
            // Equivalent C#:
            // requestInfo.AddMethodHeader("value");

            // Stack: [..., requestInfo, requestInfo]
            methodIlGenerator.Emit(OpCodes.Dup);
            // Stack: [..., requestInfo, requestInfo, "value"]
            methodIlGenerator.Emit(OpCodes.Ldstr, header.Value);
            // Stack: [..., requestInfo]
            methodIlGenerator.Emit(OpCodes.Callvirt, addMethodHeaderMethod);
        }

        private void AddParam(ILGenerator methodIlGenerator, string name, short parameterIndex, MethodInfo methodToCall)
        {
            // Equivalent C#:
            // requestInfo.methodToCall("name", value.ToString());
            // where 'value' is the parameter at index parameterIndex

            // Duplicate the requestInfo. This is because calling AddQueryParameter on it will pop it
            // Stack: [..., requestInfo, requestInfo]
            methodIlGenerator.Emit(OpCodes.Dup);
            // Load the name onto the stack
            // Stack: [..., requestInfo, requestInfo, name]
            methodIlGenerator.Emit(OpCodes.Ldstr, name);
            // Load the param onto the stack
            // Stack: [..., requestInfo, requestInfo, name, value]
            methodIlGenerator.Emit(OpCodes.Ldarg, parameterIndex);
            // Call ToString on the value
            // Stack: [..., requestInfo, requestInfo, name, valueAsString]
            methodIlGenerator.Emit(OpCodes.Callvirt, toStringMethod);
            // Call AddPathParameter
            // Stack: [..., requestInfo]
            methodIlGenerator.Emit(OpCodes.Callvirt, methodToCall);
        }

        private void ValidatePathParams(string path, IEnumerable<string> pathParams)
        {
            var pathPartsSet = new HashSet<string>(pathParamMatch.Matches(path).Cast<Match>().Select(x => x.Groups[1].Value));
            pathPartsSet.SymmetricExceptWith(pathParams);
            var firstInvalid = pathPartsSet.FirstOrDefault();
            if (firstInvalid != null)
                throw new RestEaseImplementationCreationException(String.Format("Unable to find both a placeholder {{{0}}} and a [PathParam(\"{0}\")] for parameter {0}", firstInvalid));
        }
    }
}

