using Data;
using Data.Actions.Notify;
using Data.Actions.Notify.SoundElements;
using Data.Events;
using Data.Events.Abnormality;
using Nostrum;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Tera.Game;

namespace DamageMeter.UI
{
    public enum SoundType
    {
        None,
        Beeps,
        Music,
        TTS
    }

    public class EventsEditorViewModel : TSPropertyChanged
    {
        public static IEnumerable<PlayerClass> Classes = EnumUtils.ListFromEnum<PlayerClass>();
        public static IEnumerable<EventType> EventTypes = EnumUtils.ListFromEnum<EventType>();
        public static IEnumerable<SoundType> SoundTypes = EnumUtils.ListFromEnum<SoundType>();

        public static IEnumerable<VoiceGender> Genders
        {
            get
            {
                var ret = EnumUtils.ListFromEnum<VoiceGender>();
                ret.Remove(VoiceGender.NotSet);
                return ret;

            }
        }


        public static IEnumerable<VoiceAge> Ages
        {
            get
            {
                var ret = EnumUtils.ListFromEnum<VoiceAge>();
                ret.Remove(VoiceAge.NotSet);
                return ret;

            }
        }

        public static IEnumerable<AbnormalityTargetType> TargetTypes = EnumUtils.ListFromEnum<AbnormalityTargetType>();
        public static IEnumerable<AbnormalityTriggerType> TriggerTypes = EnumUtils.ListFromEnum<AbnormalityTriggerType>();

        public static Color SelfColor => BasicTeraData.Instance.WindowData.PlayerColor;

        private readonly EventsData _data;
        private readonly Dispatcher _dispatcher;
        private readonly List<BaseEventViewModel> _allEvents;
        private CancellationTokenSource? _filterCancellation;
        private int _filterVersion;
        private string _searchText = string.Empty;
        private bool _showActiveOnly;
        private int _visibleEventCount;
        private string _newAbnormalityId = string.Empty;
        private int _newAbnormalityStacks;

        public ICommand LoadCommand { get; }
        public ICommand ApplyCommand { get; }
        public ICommand AddAbnormalityEventCommand { get; }
        public ICommand RemoveEventCommand { get; }
        public ICommand ResetToDefaultCommand { get; }

        public SynchronizedObservableCollection<BaseEventViewModel> CommonEvents { get; }
        public SynchronizedObservableCollection<BaseEventViewModel> VisibleEvents { get; }

        public string SearchText
        {
            get => _searchText;
            set
            {
                if (_searchText == value) return;
                _searchText = value;
                NotifyPropertyChanged();
                RefreshEventsView();
            }
        }

        public bool ShowActiveOnly
        {
            get => _showActiveOnly;
            set
            {
                if (_showActiveOnly == value) return;
                _showActiveOnly = value;
                NotifyPropertyChanged();
                RefreshEventsView();
            }
        }

        public int VisibleEventCount
        {
            get => _visibleEventCount;
            private set
            {
                if (_visibleEventCount == value) return;
                _visibleEventCount = value;
                NotifyPropertyChanged();
                NotifyPropertyChanged(nameof(HasNoResults));
                NotifyPropertyChanged(nameof(ResultSummary));
            }
        }

        public bool HasNoResults => VisibleEventCount == 0;

        public string ResultSummary => $"{VisibleEventCount} of {CommonEvents.Count} events";

        public string EmptySearchMessage
        {
            get
            {
                if (SearchText?.IndexOf("nostrum", StringComparison.InvariantCultureIgnoreCase) >= 0)
                {
                    return "No events match. Nostrum alerts were removed from the Classic+ preset because they do not apply to this version.";
                }

                return "No events match. Clear search or turn off Active only.";
            }
        }

        public string NewAbnormalityId
        {
            get => _newAbnormalityId;
            set
            {
                if (_newAbnormalityId == value) return;
                _newAbnormalityId = value;
                NotifyPropertyChanged();
            }
        }

        public int NewAbnormalityStacks
        {
            get => _newAbnormalityStacks;
            set
            {
                if (_newAbnormalityStacks == value) return;
                _newAbnormalityStacks = value;
                NotifyPropertyChanged();
            }
        }

        public EventsEditorViewModel() : base(Dispatcher.CurrentDispatcher)
        {
            _dispatcher = Dispatcher.CurrentDispatcher;
            _data = BasicTeraData.Instance.EventsData;
            _allEvents = new List<BaseEventViewModel>();

            CommonEvents = new SynchronizedObservableCollection<BaseEventViewModel>(_dispatcher);
            VisibleEvents = new SynchronizedObservableCollection<BaseEventViewModel>(_dispatcher);

            LoadCommand = new RelayCommand(_ => Load());
            ApplyCommand = new RelayCommand(_ => Apply());
            AddAbnormalityEventCommand = new RelayCommand(_ => AddAbnormalityEvent());
            RemoveEventCommand = new RelayCommand(ev => RemoveEvent(ev as BaseEventViewModel));
            ResetToDefaultCommand = new RelayCommand(_ => ResetToDefault());

            Load();
        }

        private void Apply()
        {
            _data.Save();
            // Active flags edited here only reach the notify processor once the collections it
            // iterates are rebuilt.
            _data.RefreshActiveEvents();
        }

        private void Load()
        {
            CommonEvents.Clear();
            VisibleEvents.Clear();
            _allEvents.Clear();

            // load data from model
            foreach (var (commonEvent, actions) in _data.EventsCommon)
            {
                AddEvent(commonEvent switch
                {
                    AbnormalityEvent ab => new AbnormalityEventViewModel(ab, actions),
                    CooldownEvent cd => new CooldownEventViewModel(cd, actions),
                    CommonAFKEvent afk => new AfkEventViewModel(afk, actions),
                    _ => throw new ArgumentOutOfRangeException()
                });
            }

            RefreshEventsView();
        }

        private void AddAbnormalityEvent()
        {
            if (!int.TryParse(NewAbnormalityId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) || id <= 0)
            {
                return;
            }

            var abnormalityEvent = new AbnormalityEvent(
                true,
                false,
                5,
                new List<BlackListItem>(),
                new Dictionary<int, int> { { id, Math.Max(0, NewAbnormalityStacks) } },
                new List<HotDot.Types>(),
                AbnormalityTargetType.Self,
                AbnormalityTriggerType.Added,
                0,
                0,
                false,
                new List<PlayerClass>());
            var actions = new List<Data.Actions.Action> { new NotifyAction(null, null) };

            _data.AddCommonEvent(abnormalityEvent, actions);
            var vm = new AbnormalityEventViewModel(abnormalityEvent, actions) { IsExpanded = true };
            AddEvent(vm);

            NewAbnormalityId = string.Empty;
            NewAbnormalityStacks = 0;
            RefreshEventsView();
        }

        private void RemoveEvent(BaseEventViewModel ev)
        {
            if (ev == null) { return; }

            _data.RemoveCommonEvent(ev.SourceEvent);
            _allEvents.Remove(ev);
            CommonEvents.Remove(ev);
            VisibleEvents.Remove(ev);
            RefreshEventCounts();
        }

        private void ResetToDefault()
        {
            _data.ResetCommonToDefault();
            Load();
        }

        private void AddEvent(BaseEventViewModel ev)
        {
            ev.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(BaseEventViewModel.Active) || args.PropertyName == nameof(BaseEventViewModel.SearchText))
                {
                    RefreshEventsView();
                }
            };
            _allEvents.Add(ev);
            CommonEvents.Add(ev);
        }

        private static bool FilterEvent(BaseEventViewModel ev, bool showActiveOnly, string searchText)
        {
            if (showActiveOnly && !ev.Active) return false;
            if (string.IsNullOrWhiteSpace(searchText)) return true;
            return CultureInfo.InvariantCulture.CompareInfo.IndexOf(
                ev.SearchText ?? string.Empty,
                searchText,
                CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0;
        }

        private void RefreshEventsView()
        {
            _filterCancellation?.Cancel();
            var cancellation = new CancellationTokenSource();
            _filterCancellation = cancellation;

            var version = ++_filterVersion;
            var searchText = SearchText;
            var showActiveOnly = ShowActiveOnly;
            var events = _allEvents.ToArray();

            Task.Run(() =>
            {
                cancellation.Token.ThrowIfCancellationRequested();
                return events.Where(ev => FilterEvent(ev, showActiveOnly, searchText)).ToList();
            }, cancellation.Token).ContinueWith(task =>
            {
                if (task.IsCanceled || task.IsFaulted) return;

                _dispatcher.BeginInvoke(new Action(() =>
                {
                    if (version != _filterVersion || cancellation.IsCancellationRequested) return;
                    VisibleEvents.ReplaceWith(task.Result);
                    RefreshEventCounts();
                    NotifyPropertyChanged(nameof(EmptySearchMessage));
                }), DispatcherPriority.Background);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

            NotifyPropertyChanged(nameof(EmptySearchMessage));
        }

        private void RefreshEventCounts()
        {
            VisibleEventCount = VisibleEvents.Count;
            NotifyPropertyChanged(nameof(ResultSummary));
            NotifyPropertyChanged(nameof(HasNoResults));
        }
    }

    public class SoundTemplateSelector : DataTemplateSelector
    {
        public DataTemplate MusicDataTemplate { get; set; }
        public DataTemplate BeepsDataTemplate { get; set; }
        public DataTemplate TtsDataTemplate { get; set; }

        public override DataTemplate SelectTemplate(object item, DependencyObject container)
        {
            return item switch
            {
                MusicDataVM => MusicDataTemplate,
                BeepsDataVM => BeepsDataTemplate,
                TtsDataVM => TtsDataTemplate,
                _ => null
            };
        }
    }

    public class EventTemplateSelector : DataTemplateSelector
    {
        public DataTemplate AbnormalityDataTemplate { get; set; }
        public DataTemplate CooldownDataTemplate { get; set; }
        public DataTemplate AfkDataTemplate { get; set; }

        public override DataTemplate SelectTemplate(object item, DependencyObject container)
        {
            return item switch
            {
                AbnormalityEventViewModel => AbnormalityDataTemplate,
                CooldownEventViewModel => CooldownDataTemplate,
                AfkEventViewModel => AfkDataTemplate,
                _ => null
            };
        }
    }
}
