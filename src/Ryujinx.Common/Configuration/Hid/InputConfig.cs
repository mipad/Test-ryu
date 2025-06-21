using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Ryujinx.Common.Configuration.Hid
{
    [JsonConverter(typeof(JsonInputConfigConverter))]
    public class InputConfig : INotifyPropertyChanged
    {
        /// <summary>
        /// The current version of the input file format
        /// </summary>
        public const int CurrentVersion = 1;

        public int Version { get; set; }

        public InputBackendType Backend { get; set; }

        /// <summary>
        /// Controller id
        /// </summary>
        public string Id { get; set; }

        /// <summary>
        ///  Controller's Type
        /// </summary>
        public ControllerType ControllerType { get; set; }

        /// <summary>
        ///  Player's Index for the controller
        /// </summary>
        public PlayerIndex PlayerIndex { get; set; }

        /// <summary>
        /// 表示此配置是否为手持模式控制器
        /// </summary>
        public bool IsHandheld
        {
            get => ControllerType == ControllerType.Handheld;
            set
            {
                if (value)
                {
                    ControllerType = ControllerType.Handheld;
                }
                else if (ControllerType == ControllerType.Handheld)
                {
                    ControllerType = ControllerType.ProController; // 默认回退
                }
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
