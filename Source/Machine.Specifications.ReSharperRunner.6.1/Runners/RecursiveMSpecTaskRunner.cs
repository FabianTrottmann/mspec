using System;
using System.IO;
using System.Reflection;

using JetBrains.ReSharper.TaskRunnerFramework;

using Machine.Specifications.ReSharperRunner.Runners.Notifications;
using Machine.Specifications.ReSharperRunner.Tasks;
using Machine.Specifications.Runner;
using Machine.Specifications.Runner.Impl;
using Machine.Specifications.Utility;

namespace Machine.Specifications.ReSharperRunner.Runners
{
  class RunScope
  {
    public Action<Assembly> StartRun { get; set; }
    public Action<Assembly> EndRun { get; set; }

    public RunScope()
    {
      StartRun = x => { };
      EndRun = x => { };
    }
  }

  internal class RecursiveMSpecTaskRunner : RecursiveRemoteTaskRunner
  {
    readonly RemoteTaskNotificationFactory _taskNotificationFactory = new RemoteTaskNotificationFactory();
    Assembly _contextAssembly;
    Type _contextClass;
    PerAssemblyRunListener _listener;
    DefaultRunner _runner;
    RunScope _runScope;

    public RecursiveMSpecTaskRunner(IRemoteTaskServer server) : base(server)
    {
    }

    public override TaskResult Start(TaskExecutionNode node)
    {
      var task = (RunAssemblyTask) node.RemoteTask;

      _contextAssembly = LoadContextAssembly(task);
      if (_contextAssembly == null)
      {
        return TaskResult.Error;
      }

      var result = VersionCompatibilityChecker.Check(_contextAssembly);
      if (!result.Success)
      {
        Server.TaskExplain(task, result.Explanation);
        Server.TaskError(task, result.ErrorMessage);

        return TaskResult.Error;
      }

      _listener = new PerAssemblyRunListener(Server, task);

      _runner = new DefaultRunner(_listener, RunOptions.Default);
      _runScope = GetRunScope(_runner);

      return TaskResult.Success;
    }

    static RunScope GetRunScope(DefaultRunner runner)
    {
      var scope = new RunScope();

      var runnerType = runner.GetType();

      var startRun = runnerType.GetMethod("StartRun", new[]{typeof(Assembly)});
      if (startRun != null)
      {
        scope.StartRun = asm => startRun.Invoke(runner, new object[] { asm });
      }

      var endRun = runnerType.GetMethod("EndRun", new[] {typeof(Assembly)});
      if (endRun != null)
      {
        scope.EndRun = asm => endRun.Invoke(runner, new object[] { asm });
      }

      return scope;
    }

    public override TaskResult Execute(TaskExecutionNode node)
    {
      // This method is never called.
      return TaskResult.Success;
    }

    public override void ExecuteRecursive(TaskExecutionNode node)
    {
      node.Flatten(x => x.Children).Each(RegisterRemoteTaskNotifications);
    }

    public override TaskResult Finish(TaskExecutionNode node)
    {
      try
      {
        _runScope.StartRun(_contextAssembly);

        foreach (var child in node.Children)
        {
          RunContext(child);
        }

        return TaskResult.Success;
      }
      finally
      {
        _runScope.EndRun(_contextAssembly);
      }
    }

    void RunContext(TaskExecutionNode node)
    {
      var task = (ContextTask) node.RemoteTask;

      _contextClass = _contextAssembly.GetType(task.ContextTypeName);
      if (_contextClass == null)
      {
        Server.TaskExplain(task,
                           String.Format("Could not load type '{0}' from assembly {1}.",
                                         task.ContextTypeName,
                                         task.AssemblyLocation));
        Server.TaskError(node.RemoteTask, "Could not load context");
        return;
      }

      _runner.RunMember(_contextAssembly, _contextClass);
    }

    void RegisterRemoteTaskNotifications(TaskExecutionNode node)
    {
      _listener.RegisterTaskNotification(_taskNotificationFactory.CreateTaskNotification(node));
    }

    Assembly LoadContextAssembly(RunAssemblyTask task)
    {
      AssemblyName assemblyName;
      if (!File.Exists(task.AssemblyLocation))
      {
        Server.TaskExplain(task,
                           String.Format("Could not load assembly from {0}: File does not exist", task.AssemblyLocation));
        Server.TaskError(task, "Could not load context assembly");
        return null;
      }

      try
      {
        assemblyName = AssemblyName.GetAssemblyName(task.AssemblyLocation);
      }
      catch (FileLoadException ex)
      {
        Server.TaskExplain(task,
                           String.Format("Could not load assembly from {0}: {1}", task.AssemblyLocation, ex.Message));
        Server.TaskError(task, "Could not load context assembly");
        return null;
      }

      if (assemblyName == null)
      {
        Server.TaskExplain(task,
                           String.Format("Could not load assembly from {0}: Not an assembly", task.AssemblyLocation));
        Server.TaskError(task, "Could not load context assembly");
        return null;
      }

      try
      {
        return Assembly.Load(assemblyName);
      }
      catch (Exception ex)
      {
        Server.TaskExplain(task,
                           String.Format("Could not load assembly from {0}: {1}", task.AssemblyLocation, ex.Message));
        Server.TaskError(task, "Could not load context assembly");
        return null;
      }
    }
  }
}