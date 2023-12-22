using Microsoft.Extensions.DependencyInjection;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;


namespace ChangeVolumeMixerForWebView
{
	/// <summary>
	/// Interaction logic for MainWindow.xaml
	/// </summary>
	public partial class MainWindow : Window, IAudioSessionEventsHandler
	{
		private MMDevice? mainDevice;
		private AudioSessionControl? currentSession;

		public MainWindow()
		{
			InitializeComponent();

			//Setup of Blazor.
			IServiceCollection services = new ServiceCollection();
			services.AddWpfBlazorWebView();
			services.AddBlazorWebViewDeveloperTools();
			var sp = services.BuildServiceProvider();
			Resources.Add("services", sp);
		}

		private void Window_Loaded(object sender, RoutedEventArgs e)
		{
			try
			{
				//Connect to audio device and get all audio session and try to change the text and icon.
				var etor = new MMDeviceEnumerator();
				this.mainDevice = etor.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
				if (mainDevice is null)
				{
					return;
				}

				var sessions = mainDevice.AudioSessionManager.Sessions;
				for (int i = 0; i < sessions.Count; i++)
				{
					var session = sessions[i];
					ChangeTextAndIcon(session);
				}

				//Listen for the creation of a new audio session, because likely there's none yet 
				//for our app. As soon as we are playing sound the event is fired and we can then change the text and icon.
				mainDevice.AudioSessionManager.OnSessionCreated += AudioSessionManager_OnSessionCreated;

			}
			catch (Exception ex)
			{
				Debug.WriteLine(ex.ToString());
				MessageBox.Show(this, ex.ToString(), this.Title);
			}
		}


		private void Window_Closed(object sender, EventArgs e)
		{
			try
			{
				var sess = this.currentSession;
				this.currentSession = null;
				this.UnregisterFromSession(sess);
				if (mainDevice != null)
				{
					mainDevice.AudioSessionManager.OnSessionCreated -= this.AudioSessionManager_OnSessionCreated;
					mainDevice.Dispose();
				}
			}
			catch (Exception ex)
			{
				Debug.WriteLine(ex.ToString());
			}
		}


		/// <summary>
		/// Changes the text and icon of our application in the volume mixer (sndvol.exe).
		/// Note that this method can be called from a different thread than the UI thread.
		/// </summary>
		/// <param name="session">Any audio session control</param>
		private void ChangeTextAndIcon(AudioSessionControl session)
		{
			try
			{
				//If it's the sys sound session we can discard it right away
				if (session.IsSystemSoundsSession) return;
				var proc = Process.GetProcessById((int)session.GetProcessID);
				if (proc == null)
				{
					return;
				}

				//Get the process that creates the audio session. 
				//WebView2 starts as a subprocess of our process, therefore
				//we ensure that the audio session really belongs to our app.
				var currentProc = Process.GetCurrentProcess();
				Process? parentProc = proc;
				bool isSubProc = false;
				while (parentProc != null && parentProc.Id != 0)
				{
					parentProc = GetParentProcess(parentProc);
					if (parentProc != null && parentProc.Id == currentProc.Id)
					{
						isSubProc = true;
						break;
					}
				}
				if (!isSubProc)
				{
					return;
				}

				//Change text
				session.DisplayName = "My Player";
				//Change location. This change is not reflected in an opened volume mixer. 
				//It's only visible after the volume mixer has been (re-)opened
				session.IconPath = GetType().Assembly.Location;
				this.currentSession = session;
				//Subscribe to events of the session in order to eventually 
				this.currentSession.RegisterEventClient(this);
			}
			catch (Exception ex)
			{
				Debug.WriteLine(ex.ToString());
				//Since we could have entered from an audio event we may be on a diferent thread.
				_ = this.Dispatcher.InvokeAsync(() =>
				{
					MessageBox.Show(this, ex.ToString(), this.Title);
				});
			}
		}

		private void AudioSessionManager_OnSessionCreated(object sender, IAudioSessionControl newSession)
		{
			AudioSessionControl managedControl = new AudioSessionControl(newSession);
			ChangeTextAndIcon(managedControl);
		}

		/// <summary>
		/// Gets the parent proccess for a given <paramref name="process"/>.
		/// </summary>
		/// <param name="process"></param>
		/// <returns></returns>
		private Process? GetParentProcess(Process process)
		{
			try
			{
				using (var query = new ManagementObjectSearcher(
				  "SELECT * " +
				  "FROM Win32_Process " +
				  "WHERE ProcessId=" + process.Id))
				{
					using (var collection = query.Get())
					{
						var mo = collection.OfType<ManagementObject>().FirstOrDefault();
						if (mo != null)
						{
							using (mo)
							{
								var p = Process.GetProcessById((int)(uint)mo["ParentProcessId"]);
								return p;
							}
						}

					}
					return null;
				}
			}
			catch
			{
				return null;
			}
		}

		void IAudioSessionEventsHandler.OnVolumeChanged(float volume, bool isMuted) { }
		void IAudioSessionEventsHandler.OnDisplayNameChanged(string displayName) { }
		void IAudioSessionEventsHandler.OnChannelVolumeChanged(uint channelCount, nint newVolumes, uint channelIndex) { }
		void IAudioSessionEventsHandler.OnGroupingParamChanged(ref Guid groupingId) { }

		void IAudioSessionEventsHandler.OnIconPathChanged(string iconPath)
		{
			Debug.WriteLine("OnIconPathChanged: " + iconPath);
		}

		void IAudioSessionEventsHandler.OnStateChanged(AudioSessionState state)
		{
			if (state == AudioSessionState.AudioSessionStateExpired)
			{
				//I never observed that this occured. Therefore I am not sure if it is
				//the right way to relase our session. 
				this.ReleaseSessionDelayed(this.currentSession);
			}
		}

		void IAudioSessionEventsHandler.OnSessionDisconnected(AudioSessionDisconnectReason disconnectReason)
		{
			//I never observed that this occured. Therefore I am not sure if it is
			//the right way to relase our session. 
			this.ReleaseSessionDelayed(this.currentSession);
		}

		/// <summary>
		/// Release our audio session, but don't do it just now, since we enter this
		/// from an event of <see cref="IAudioSessionEventsHandler"/> and the SDSK
		/// states that be must never unregister from the session from within such an event.
		/// </summary>
		/// <param name="session"></param>
		private void ReleaseSessionDelayed(AudioSessionControl? session)
		{
			//Marshal it to the UI thread.
			_ = this.Dispatcher.InvokeAsync(() =>
			{
				//Kind of paranoid delay of the actual releasing.
				DispatcherTimer timer = new DispatcherTimer();
				void TimerTick(object? sender, EventArgs e)
				{
					timer.Stop();
					timer.Tick -= TimerTick;
					UnregisterFromSession(session);
				};
				timer.Tick += TimerTick;
				timer.Interval = TimeSpan.FromMilliseconds(100);
			});
		}

		private void UnregisterFromSession(AudioSessionControl? session)
		{
			try
			{
				if (session != null)
				{
					session.UnRegisterEventClient(this);
					session.Dispose();
				}
			}
			catch (Exception ex)
			{
				Debug.WriteLine(ex.ToString());
			}
		}
	}
}