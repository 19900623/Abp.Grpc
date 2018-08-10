﻿using Abp.Dependency;
using Abp.Grpc.Client.Configuration;
using Abp.Grpc.Client.Infrastructure.GrpcChannel;
using Abp.Grpc.Client.Installer;
using Abp.Grpc.Common.Infrastructure;
using Abp.Modules;
using Abp.Threading;
using Grpc.Core;
using Polly;
using System.Collections.Generic;
using System.Linq;

namespace Abp.Grpc.Client
{
    [DependsOn(typeof(AbpKernelModule))]
    public class AbpGrpcClientModule : AbpModule
    {
        private IGrpcClientConfiguration _grpcClientConfiguration;

        public override void PreInitialize()
        {
            IocManager.IocContainer.Install(new AbpGrpcClientInstaller());
        }

        public override void Initialize()
        {
            IocManager.RegisterAssemblyByConvention(typeof(AbpGrpcClientModule).Assembly,
                new ConventionalRegistrationConfig
                {
                    InstallInstallers = false
                });
        }

        public override void PostInitialize()
        {
            _grpcClientConfiguration = IocManager.Resolve<IGrpcClientConfiguration>();

            if (!_grpcClientConfiguration.IsDebugMode)
            {
                ScanAllAvailableGrpcServices(_grpcClientConfiguration);
            }
        }

        public override void Shutdown()
        {
            if (!_grpcClientConfiguration.IsDebugMode)
            {
                // 遍历所有已注册的 Grpc 服务
                foreach (var channels in _grpcClientConfiguration.GrpcServers.Values)
                {
                    // 遍历频道并关闭
                    foreach (var channel in channels)
                    {
                        channel.ShutdownAsync().GetAwaiter().GetResult();
                    }
                }
            }
        }

        /// <summary>
        /// 扫描所有可用的远端服务
        /// </summary>
        /// <param name="config">配置项</param>
        private void ScanAllAvailableGrpcServices(IGrpcClientConfiguration config)
        {
            var consulClient = IocManager.Resolve<IConsulClientFactory>().Get(config.ConsulRegistryConfiguration);
            var grpcChannelFactory = IocManager.Resolve<IGrpcChannelFactory>();

            var policy = Policy.Timeout(5, (context, span, arg3) => throw new AbpInitializationException("无法连接到 Consul 集群."));

            policy.Execute(() =>
            {
                AsyncHelper.RunSync(async () =>
                {
                    var services = await consulClient.Catalog.Services();
                    foreach (var service in services.Response)
                    {
                        var serviceInfo = await consulClient.Catalog.Service(service.Key);
                        var grpcServerInfo = serviceInfo.Response.SkipWhile(z => !z.ServiceTags.Contains("Grpc"));

                        //TODO: 此处可做负载均衡
                        foreach (var info in grpcServerInfo)
                        {
                            if (!_grpcClientConfiguration.GrpcServers.ContainsKey(info.ServiceName))
                            {
                                _grpcClientConfiguration.GrpcServers.Add(info.ServiceName,
                                    new List<Channel> { grpcChannelFactory.Get(info.ServiceAddress, info.ServicePort) });
                            }
                            else
                            {
                                _grpcClientConfiguration.GrpcServers[info.ServiceName]
                                    .Add(grpcChannelFactory.Get(info.ServiceAddress, info.ServicePort));
                            }
                        }
                    }
                });
            });
        }
    }
}