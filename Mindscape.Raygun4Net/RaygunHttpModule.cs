using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Web;
using Mindscape.Raygun4Net.ProfilingSupport;

namespace Mindscape.Raygun4Net
{
  public class RaygunHttpModule : IHttpModule
  {
    private static readonly TimeSpan AgentPollingDelay = new TimeSpan(0, 5, 0);
    private static ISamplingManager _samplingManager;

    private bool ExcludeErrorsBasedOnHttpStatusCode { get; set; }
    private bool ExcludeErrorsFromLocal { get; set; }

    private int[] HttpStatusCodesToExclude { get; set; }

    public void Init(HttpApplication context)
    {
      context.Error += SendError;
      context.BeginRequest += BeginRequest;
      context.EndRequest += EndRequest;

      HttpStatusCodesToExclude = RaygunSettings.Settings.ExcludedStatusCodes;
      ExcludeErrorsBasedOnHttpStatusCode = HttpStatusCodesToExclude.Any();
      ExcludeErrorsFromLocal = RaygunSettings.Settings.ExcludeErrorsFromLocal;

      var mvcAssembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.FullName.StartsWith("Mindscape.Raygun4Net.Mvc,"));
      if (mvcAssembly != null)
      {
        var type = mvcAssembly.GetType("Mindscape.Raygun4Net.RaygunExceptionFilterAttacher");
        var method = type.GetMethod("AttachExceptionFilter", BindingFlags.Static | BindingFlags.Public);
        method.Invoke(null, new object[] { context, this });
      }

      InitProfilingSupport();
    }

    private void BeginRequest(object sender, EventArgs e)
    {
      try
      {
        if (_samplingManager != null)
        {
          var application = (HttpApplication) sender;

          if (!_samplingManager.TakeSample(application.Request.Url))
          {
            APM.Disable();
          }
        }
      }
      catch (Exception ex)
      {
        System.Diagnostics.Trace.WriteLine($"Error during begin request of APM sampling: {ex.Message}");
      }
    }

    private void EndRequest(object sender, EventArgs e)
    {
      try
      {
        if (_samplingManager != null)
        {
          APM.Enable();
        }
      }
      catch (Exception ex)
      {
        System.Diagnostics.Trace.WriteLine($"Error during end request of APM sampling: {ex.Message}");
      }
    }

    private void InitProfilingSupport()
    {
      try
      {
        lock (this)
        {
          if (_samplingManager == null)
          {
            if (APM.ProfilerAttached)
            {
              System.Diagnostics.Trace.WriteLine("Detected Raygun APM profiler is attached, initializing profiler support.");

              var thread = new Thread(RefreshAgentSettings);
              // ensure that our thread *doesn't* keep the application alive!
              thread.IsBackground = true;
              thread.Start();

              _samplingManager = new SamplingManager();
            }
          }
        }
      }
      catch (Exception ex)
      {
        System.Diagnostics.Trace.WriteLine($"Error initialising APM profiler support: {ex.Message}");
      }
    }

    private static readonly string SettingsFilePath =
      Path.Combine(
        Path.Combine(
          Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Raygun"),
          "AgentSettings"),
        "agent-configuration.json");

    private void RefreshAgentSettings()
    {
      while (true)
      {
        try
        {
          if (File.Exists(SettingsFilePath))
          {
            var settingsText = File.ReadAllText(SettingsFilePath);
            var siteName = System.Web.Hosting.HostingEnvironment.SiteName;
            
            var samplingSetting = SettingsManager.ParseSamplingSettings(settingsText, siteName);
            if (samplingSetting != null)
            {
              _samplingManager.SetSamplingPolicy(samplingSetting.Policy, samplingSetting.Overrides);
            }
            else
            {
              System.Diagnostics.Trace.WriteLine($"Could not locate sampling settings for site {siteName}");
            }
          }
          else
          {
            System.Diagnostics.Trace.WriteLine($"Could not locate Raygun APM configuration file {SettingsFilePath}");
          }
        }
        catch (ThreadAbortException)
        {
          return;
        }
        catch (Exception ex)
        {
          System.Diagnostics.Trace.WriteLine($"Error refreshing agent settings: {ex.Message}");
        }

        Thread.Sleep(AgentPollingDelay);
      }
    }

    public void Dispose()
    {
    }

    private void SendError(object sender, EventArgs e)
    {
      var application = (HttpApplication)sender;
      var lastError = application.Server.GetLastError();

      if (CanSend(lastError))
      {
        var client = GetRaygunClient(application);
        client.SendInBackground(lastError, new List<string> {RaygunClient.UnhandledExceptionTag});
      }
    }

    public void SendError(HttpApplication application, Exception exception)
    {
      if (CanSend(exception))
      {
        var client = GetRaygunClient(application);
        client.SendInBackground(exception, new List<string> { RaygunClient.UnhandledExceptionTag });
      }
    }

    protected RaygunClient GetRaygunClient(HttpApplication application)
    {
      var raygunApplication = application as IRaygunApplication;
      return raygunApplication != null ? raygunApplication.GenerateRaygunClient() : GenerateDefaultRaygunClient(application);
    }

    private RaygunClient GenerateDefaultRaygunClient(HttpApplication application)
    {
      var instance = new RaygunClient();

      if (HttpContext.Current != null && HttpContext.Current.Session != null)
      {
        instance.ContextId = HttpContext.Current.Session.SessionID;
      }

      return instance;
    }

    protected bool CanSend(Exception exception)
    {
      if (ExcludeErrorsBasedOnHttpStatusCode && exception is HttpException && HttpStatusCodesToExclude.Contains(((HttpException)exception).GetHttpCode()))
      {
        return false;
      }

      if (ExcludeErrorsFromLocal && HttpContext.Current.Request.IsLocal)
      {
        return false;
      }

      return true;
    }
  }
}
