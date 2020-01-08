using Entity;
using Hydrology.Entity;
using Protocol.Channel.Interface;
using Protocol.Data.Interface;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Timers;



namespace Protocol.Channel.HDGprs
{
    public class HDGpesParser : IHDGprs
    {
        public static int MAX_BUFFER = 1024;
        internal class MyMessage
        {
            public string ID;
            public string MSG;
        }
        #region 成员变量
        static bool s_isFirstSend = true;
        private Semaphore m_semaphoreData;    //用来唤醒消费者处理缓存数据
        private Mutex m_mutexListDatas;     // 内存data缓存的互斥量
        private Thread m_threadDealData;    // 处理数据线程
        private List<HDModemDataStruct> m_listDatas;   //存放data的内存缓存

        private System.Timers.Timer m_timer = new System.Timers.Timer()
        {
            Enabled = true,
            Interval = 5000
        };
        private int GetReceiveTimeOut()
        {
            return (int)(m_timer.Interval);
        }

        public static CDictionary<String, String> HdProtocolMap = new CDictionary<string, string>();
        #endregion

        //引用PD0解析计算TODO
        [DllImport(".\\PD0FileReader.dll")]
        public static extern void ReadPD0FileTest();

        #region 构造方法
        public HDGpesParser()
        {
            m_semaphoreData = new Semaphore(0, Int32.MaxValue);
            m_listDatas = new List<HDModemDataStruct>();
            m_mutexListDatas = new Mutex();

            m_threadDealData = new Thread(new ThreadStart(this.DealData));
            m_threadDealData.Start();

            DTUList = new List<HDModemInfoStruct>();

            m_timer.Elapsed += new ElapsedEventHandler(m_timer_Elapsed);
        }
        #endregion
        void m_timer_Elapsed(object sender, ElapsedEventArgs e)
        {
            int second = GetReceiveTimeOut();
            InvokeMessage(String.Format("系统接收数据时间超过{0}毫秒", second), "系统超时");
            if (this.ErrorReceived != null)
                this.ErrorReceived.Invoke(null, new ReceiveErrorEventArgs()
                {
                    Msg = String.Format("系统接收数据时间超过{0}秒", second)
                });
            if (null != this.GPRSTimeOut)
            {
                this.GPRSTimeOut(null, new ReceivedTimeOutEventArgs() { Second = second });
            }
            Debug.WriteLine("系统超时,停止计时器");
            m_timer.Stop();
        }
        #region 属性
        private List<CEntityStation> m_stationLists;
        public IUp Up { get; set; }
        public IDown Down { get; set; }
        public IUBatch UBatch { get; set; }
        public IFlashBatch FlashBatch { get; set; }
        public ISoil Soil { get; set; }

        public List<HDModemInfoStruct> DTUList { get; set; }

        public bool IsCommonWorkNormal { get; set; }
        private System.Timers.Timer tmrData;
        private System.Timers.Timer tmrDTU;
        private EChannelType m_channelType;
        private EListeningProtType m_portType;
        #endregion

        #region 日志记录
        public void InvokeMessage(string msg, string description)
        {
            if (this.MessageSendCompleted != null)
                this.MessageSendCompleted(null, new SendOrRecvMsgEventArgs()
                {
                    ChannelType = this.m_channelType,
                    Msg = msg,
                    Description = description
                });
        }
        #endregion



        #region 事件
        public event EventHandler<BatchEventArgs> BatchDataReceived;
        public event EventHandler<BatchSDEventArgs> BatchSDDataReceived;
        public event EventHandler<DownEventArgs> DownDataReceived;
        public event EventHandler<ReceiveErrorEventArgs> ErrorReceived;
        public event EventHandler<SendOrRecvMsgEventArgs> MessageSendCompleted;
        public event EventHandler<ReceivedTimeOutEventArgs> GPRSTimeOut;
        public event EventHandler<CEventSingleArgs<CSerialPortState>> SerialPortStateChanged;
        public event EventHandler<CEventSingleArgs<CEntitySoilData>> SoilDataReceived;
        public event EventHandler<UpEventArgs> UpDataReceived;
        public event EventHandler<UpEventArgs_new> UpDataReceived_new;
        public event EventHandler<ModemDataEventArgs> ModemDataReceived;
        public event EventHandler HDModemInfoDataReceived;
        #endregion


        #region 用户列表维护
        private bool inDtuTicks = false;
        private void tmrDTU_Elapsed(object sender, ElapsedEventArgs e)
        {
            if (inDtuTicks) return;
            inDtuTicks = true;
            try
            {
                Dictionary<string, HDModemInfoStruct> dtuList;
                if (this.getDTUList(out dtuList) == 0)
                {
                    this.DTUList.Clear();
                    foreach (var item in dtuList)
                    {
                        this.DTUList.Add(item.Value);
                    }

                    if (this.HDModemInfoDataReceived != null)
                        this.HDModemInfoDataReceived(this, null);
                }

            }
            catch (Exception)
            {
            }
            finally
            {
                inDtuTicks = false;
            }

        }

        #endregion
        public void Init()
        {
            InitMap();
            this.m_channelType = EChannelType.GPRS;
            this.m_portType = EListeningProtType.Port;
            if (tmrData == null)
                tmrData = new System.Timers.Timer(250);
            tmrData.Elapsed += new ElapsedEventHandler(tmrData_Elapsed);

            if (tmrDTU == null)
                tmrDTU = new System.Timers.Timer(2000);
            tmrDTU.Elapsed += new ElapsedEventHandler(tmrDTU_Elapsed);

            if (DTUList == null)
                DTUList = new List<HDModemInfoStruct>();
        }
        public void Close()
        {
            this.DSStopService(null);
        }

        public void InitInterface(IUp up, IDown down, IUBatch udisk, IFlashBatch flash, ISoil soil)
        {
            this.Up = up;
            this.Down = down;
            this.UBatch = udisk;
            this.FlashBatch = flash;
            this.Soil = soil;
        }

        public void InitStations(List<CEntityStation> stations)
        {
            this.m_stationLists = stations;
        }

        public void InitMap()
        {
            String[] rows = File.ReadAllLines("Config/map.txt");
            foreach (String row in rows)
            {
                String[] pieces = row.Split(',');
                if (pieces.Length == 2)
                    if (!HdProtocolMap.ContainsKey(pieces[0]))
                    {
                        HdProtocolMap.Add(pieces[0], pieces[1]);
                    }
                    else
                    {
                        HdProtocolMap[pieces[0]] = pieces[1];
                    }
            }
        }
        private CEntityStation FindStationBySID(string sid)
        {
            if (this.m_stationLists == null)
                throw new Exception("GPRS模块未初始化站点！");

            CEntityStation result = null;
            foreach (var station in this.m_stationLists)
            {
                if (station.StationID.Equals(sid))
                {
                    result = station;
                    break;
                }
            }
            return result;
        }

        public int DSStartService(ushort port, int protocol, int mode, string mess, IntPtr ptr)
        {
            bool flag = false;
            StringBuilder mess1 = new StringBuilder();
            int started = DTUdll.Instance.StartService(port, protocol, mode, mess, ptr);
            if (started == 0)
            {
                tmrData.Start();
                tmrDTU.Start();
                flag = true;
            }
            if (SerialPortStateChanged != null)
                SerialPortStateChanged(this, new CEventSingleArgs<CSerialPortState>(new CSerialPortState()
                {
                    PortType = this.m_portType,
                    PortNumber = port,
                    BNormal = flag
                }));
            InvokeMessage(String.Format("开启端口{0}   {1}!", port, started == 0 ? "成功" : "失败"), "初始化");
            return started;
        }

        public int DSStopService(string mess)
        {
            bool stoped = false;
            int ended = 0;
            ended = DTUdll.Instance.StopService(mess);
            if (ended == 0)
            {
                stoped = true;
            }
            tmrData.Stop();
            tmrDTU.Stop();
            int port = DTUdll.Instance.ListenPort;
            if (SerialPortStateChanged != null)
                SerialPortStateChanged(this, new CEventSingleArgs<CSerialPortState>(new CSerialPortState()
                {
                    PortType = this.m_portType,
                    PortNumber = port,
                    BNormal = stoped
                }));
            InvokeMessage(String.Format("关闭端口{0}   {1}!", port, stoped ? "成功" : "失败"), "      ");
            return ended;
        }

        public int sendHex(string userid, byte[] data, uint len, string mess)
        {
            int flag = 0;
            try
            {
                flag = DTUdll.Instance.SendHex(userid, data, len, null);
                return flag;

            }
            catch (Exception)
            {
                return flag;
            }

        }

        public uint getDTUAmount()
        {
            return DTUdll.Instance.getDTUAmount();
        }
        public int getDTUInfo(string userid, out HDModemInfoStruct infoPtr)
        {
            infoPtr = new HDModemInfoStruct();
            return DTUdll.Instance.getDTUInfo(userid, out infoPtr);
        }
        public int getDTUByPosition(int index, out HDModemInfoStruct infoPtr)
        {
            infoPtr = new HDModemInfoStruct();
            return DTUdll.Instance.getDTUByPosition(index, out infoPtr);
        }
        public int getDTUList(out Dictionary<string, HDModemInfoStruct> dtuList)
        {
            return DTUdll.Instance.GetDTUList(out dtuList);
        }
        //帮助方法 20170602
        private int GetNextData(out HDModemDataStruct dat)
        {
            try
            {
                return DTUdll.Instance.GetNextData(out dat);
            }
            catch (Exception)
            {
                dat = new HDModemDataStruct();
                return -1;
            }
        }
        private bool inDataTicks = false;
        private void tmrData_Elapsed(object sender, ElapsedEventArgs e)
        {
            if (inDataTicks || inDtuTicks) return;
            inDataTicks = true;
            try
            {
                //读取数据
                HDModemDataStruct dat = new HDModemDataStruct();
                while (this.GetNextData(out dat) == 0)

                {

                    //byte[] bts = new byte[] { 84, 82, 85, 13, 10 };
                    //byte转16进制字符串

                    //1 数据字符串

                    StringBuilder adcpMsg = new StringBuilder();
                    bool flag = true;
                    int IFlag = 0;
                    foreach (byte b in dat.m_data_buf)
                    {
                        IFlag++;
                        if (b != 0 && IFlag <= 1000)
                        {
                            flag = false;
                        }
                        adcpMsg.AppendFormat("{0:x2}", b);
                    }
                    //如果全部是0
                    if (flag)
                    {
                        continue;
                    }
                    String str = adcpMsg.ToString().Trim();
                    str = str.TrimStart('0');
                    str = str.TrimEnd('0');
                    if (str.Length < 5)
                    {
                        continue;
                    }

                    //2 站点ID
                    StringBuilder modemId = new StringBuilder();
                    foreach (byte b in dat.m_modemId)
                    {
                        modemId.AppendFormat("{0:x2}", b);
                    }
                    String strid = modemId.ToString();
                    strid = System.Text.Encoding.Default.GetString(dat.m_modemId);
                    //String str = System.Text.Encoding.Default.GetString(dat.m_data_buf);
                    //String strid = System.Text.Encoding.Default.GetString(dat.m_modemId);
                    //String strTime = System.Text.Encoding.Default.GetString(dat.m_recv_time);
                    m_mutexListDatas.WaitOne();
                    if ((strid.Substring(0, 1) != "/0") && (strid.Substring(0, 1) != "\0"))
                    {
                        m_listDatas.Add(dat);
                    }
                    m_semaphoreData.Release(1);
                    m_mutexListDatas.ReleaseMutex();
                }
            }
            catch (Exception ee)
            {
                Debug.WriteLine("读取数据", ee.Message);
            }
            finally
            {
                inDataTicks = false;
            }
        }

        private void DealData()
        {
            while (true)
            {
                m_semaphoreData.WaitOne(); //阻塞当前线程，知道被其它线程唤醒
                // 获取对data内存缓存的访问权
                m_mutexListDatas.WaitOne();
                List<HDModemDataStruct> dataListTmp = m_listDatas;
                m_listDatas = new List<HDModemDataStruct>(); //开辟一快新的缓存区
                m_mutexListDatas.ReleaseMutex();
                for (int i = 0; i < dataListTmp.Count; ++i)
                {
                    try
                    {
                        HDModemDataStruct dat = dataListTmp[i];
                        //1获取gprs号码，并根据gprs号码获取站点id
                        //StringBuilder gprsId = new StringBuilder();
                        //foreach (byte b in dat.m_data_buf)
                        //{
                        //    gprsId.AppendFormat("{0:x2}", b);
                        //}
                        string gprs = System.Text.Encoding.Default.GetString(dat.m_modemId);
                        gprs = gprs.Replace("\0", "");
                        string sid = Manager.XmlStationData.Instance.GetStationByGprsID(gprs);

                        //1.2 获取ascii原始数据，获取报文头
                        string data = System.Text.Encoding.Default.GetString(dat.m_data_buf).TrimEnd('\0');
                        InvokeMessage(data, "原始数据");
                        data = data.Trim();


                        if (data.Contains("TRU"))
                        {
                            Debug.WriteLine("接收数据TRU完成,停止计时器");
                            //m_timer.Stop();
                            InvokeMessage("TRU " + System.Text.Encoding.Default.GetString(dat.m_modemId), "接收");
                            if (this.ErrorReceived != null)
                                this.ErrorReceived.Invoke(null, new ReceiveErrorEventArgs()
                                {
                                    //   Msg = "TRU " + dat.m_modemId
                                    Msg = "TRU " + System.Text.Encoding.Default.GetString(dat.m_modemId)
                                });
                        }
                        if (data.Contains("ATE0"))
                        {
                            Debug.WriteLine("接收数据ATE0完成,停止计时器");
                            //m_timer.Stop();
                            // InvokeMessage("ATE0", "接收");
                            if (this.ErrorReceived != null)
                                this.ErrorReceived.Invoke(null, new ReceiveErrorEventArgs()
                                {
                                    Msg = "ATE0"
                                });
                        }
                        string result = null;
                        if (data.Contains("$"))
                        {
                            result = data.Substring(data.IndexOf("$"));
                            int lgth = int.Parse(result.Substring(11, 4));
                            //获取报文长度
                            if (lgth > MAX_BUFFER)
                            {
                                continue;
                            }

                            result = result.Substring(0, lgth);

                            if (!(result.StartsWith("$") && result.EndsWith("\r\n")))
                            {
                                InvokeMessage(result + "报文开始符结束符不合法", "接收");
                            }
                            String dataProtocol = Manager.XmlStationData.Instance.GetProtocolBySId(sid);

                            CReportStruct report = new CReportStruct();
                            CDownConf downReport = new CDownConf();
                            if (dataProtocol == "ZYJBX")
                            {
                                Up = new Data.ZYJBX.UpParser();
                                Down = new Data.ZYJBX.DownParser();
                            }
                            if (dataProtocol == "RG30")
                            {
                                //回复TRU，确认接收数据
                                InvokeMessage("TRU " + gprs, "发送");
                                byte[] bts = new byte[] { 84, 82, 85, 13, 10 };
                                this.sendHex(gprs.Trim(), bts, (uint)bts.Length, null);


                                Up = new Data.RG30.UpParser();
                                Down = new Data.RG30.DownParser();
                                //2.1 如果是RG30,则需获取16进制字符串
                                StringBuilder adcpMsg = new StringBuilder();
                                foreach (byte b in dat.m_data_buf)
                                {
                                    adcpMsg.AppendFormat("{0:x2}", b);
                                }
                                string temp = adcpMsg.ToString().Trim();
                                if (temp.Length < 200)
                                {
                                    return;
                                }
                                InvokeMessage(temp, "原始数据");
                                //2.2 获取封装的头部信息
                                string head = temp.Substring(0, 57);
                                int length = int.Parse(temp.Substring(53, 4));

                                //2.3 根据头部信息获取数据类型  HADCP OR VADCP
                                string type = "";
                                if (head.Contains("HADCP"))
                                {
                                    type = "H";
                                }
                                else if (head.Contains("VADCP"))
                                {
                                    type = "V";
                                }
                                string hdt0 = "";
                                string vdt0 = "";
                                //2.4 根据头部信息截图DT0数据
                                if (type == "H")
                                {
                                    hdt0 = temp.Substring(57, length * 2);
                                    //写入DT0文件
                                    Write2File writeClass = new Write2File("hdt0");
                                    Thread t = new Thread(new ParameterizedThreadStart(writeClass.WriteInfoToFile));
                                    t.Start(hdt0 + "\r\n");

                                    //调用dll解析计算
                                    //TODO需要调试
                                    //ReadPD0FileTest();
                                }
                                else if (type == "V")
                                {
                                    vdt0 = temp.Substring(57, length * 2);
                                    Write2File writeClass = new Write2File("vdt0");
                                    Thread t = new Thread(new ParameterizedThreadStart(writeClass.WriteInfoToFile));
                                    t.Start(vdt0 + "\r\n");
                                }
                            }
                            //批量传输解析
                            if (data.Contains("1K"))
                            {
                                var station = FindStationBySID(sid);
                                if (station == null)
                                    throw new Exception("批量传输，站点匹配错误");
                                CBatchStruct batch = new CBatchStruct();
                                InvokeMessage(String.Format("{0,-10}   ", "批量传输") + data, "接收");

                                if (Down.Parse_Flash(result, EChannelType.GPRS, out batch))
                                {
                                    if (this.BatchDataReceived != null)
                                        this.BatchDataReceived.Invoke(null, new BatchEventArgs() { Value = batch, RawData = data });
                                }
                                else if (Down.Parse_Batch(result, out batch))
                                {
                                    if (this.BatchDataReceived != null)
                                        this.BatchDataReceived.Invoke(null, new BatchEventArgs() { Value = batch, RawData = data });
                                }
                            }
                            //+ 代表的是蒸发报文，需要特殊处理
                            //数据报文解析
                            if (result.Contains("1G21") || result.Contains("1G22") || result.Contains("1G23") ||
                                result.Contains("1G25") || result.Contains("1G29") || result.Contains("+"))
                            {
                                //回复TRU，确认接收数据
                                InvokeMessage("TRU " + gprs, "发送");
                                byte[] bts = new byte[] { 84, 82, 85, 13, 10 };
                                this.sendHex(gprs.Trim(), bts, (uint)bts.Length, null);

                                //根据$将字符串进行分割
                                var lists = result.Split('$');
                                foreach (var msg in lists)
                                {
                                    if (msg.Length < 10)
                                    {
                                        continue;
                                    }
                                    string plusMsg = "$" + msg.TrimEnd();
                                    bool ret = Up.Parse(plusMsg, out report);
                                    if (ret && report != null)
                                    {
                                        report.ChannelType = EChannelType.GPRS;
                                        report.ListenPort = this.GetListenPort().ToString();
                                        report.flagId = gprs;
                                        string rtype = report.ReportType == EMessageType.EAdditional ? "加报" : "定时报";
                                        InvokeMessage("gprs号码:  " + gprs + "   " + String.Format("{0,-10}   ", rtype) + plusMsg, "接收");
                                        //TODO 重新定义事件
                                        if (this.UpDataReceived != null)
                                        {
                                            this.UpDataReceived.Invoke(null, new UpEventArgs() { Value = report, RawData = plusMsg });
                                        }
                                    }
                                }
                            }
                            //其他报文
                            else
                            {
                                Down.Parse(result, out downReport);
                                if (downReport != null)
                                {
                                    InvokeMessage(String.Format("{0,-10}   ", "下行指令读取参数") + result, "接收");
                                    if (this.DownDataReceived != null)
                                        this.DownDataReceived.Invoke(null, new DownEventArgs() { Value = downReport, RawData = result });
                                }

                            }
                        }
                        #region 报文解析部分
                        //报文解析 直接调用
                        //if (temp.StartsWith("24") && (temp.Contains("7F7F") || temp.Contains("7f7f")) && (temp.Contains("2A2A") || temp.Contains("2a2a")))
                        ////if ((temp.Contains("7F7F") || temp.Contains("7f7f")) && (temp.Contains("2A2A") || temp.Contains("2a2a")))
                        //{
                        //    //1.获取头部信息
                        //    string headInfo = temp.Substring(0, 114);
                        //    //2.集合头信息
                        //    //2.1 计算集合头信息总长度
                        //    string setHeadInfoID = temp.Substring(114, 4);
                        //    string setHeadInfoMsgLength = temp.Substring(118, 4);
                        //    string setHeadInfoStay = temp.Substring(122, 2);
                        //    string setHeadInfoNum = temp.Substring(123, 2);
                        //    int setHeadInfoLength = 4 + 4 + 2 + 2 + 2 * (Convert.ToInt32(setHeadInfoNum,16));
                        //    string setHeadInof = temp.Substring(114, setHeadInfoLength);

                        //    //3.固定头信息
                        //    string fixHeadInfo = temp.Substring(114 + setHeadInfoLength, 118);
                        //    //3.1 获取层数信息
                        //    string speedLayersStr = fixHeadInfo.Substring(18, 2);
                        //    int speedLayers = Convert.ToInt32(speedLayersStr, 16);
                        //    //3.2 获取层厚
                        //    decimal thickness = Convert.ToInt32(fixHeadInfo.Substring(26, 2) + fixHeadInfo.Substring(24, 2), 16);
                        //    //3.3 获取盲区
                        //    decimal blindzone = Convert.ToInt32(fixHeadInfo.Substring(30, 2) + fixHeadInfo.Substring(28, 2), 16);



                        //    //4.可变头信息
                        //    string variableHeadInfo = temp.Substring(114 + 118 + setHeadInfoLength, 112);
                        //    //4.1获取数据时间
                        //    string year = "20" + Convert.ToInt32(variableHeadInfo.Substring(8, 2), 16).ToString();
                        //    string month = Convert.ToInt32(variableHeadInfo.Substring(10, 2), 16).ToString();
                        //    string day = Convert.ToInt32(variableHeadInfo.Substring(12, 2), 16).ToString();
                        //    string hour = Convert.ToInt32(variableHeadInfo.Substring(14, 2), 16).ToString();
                        //    string minute = Convert.ToInt32(variableHeadInfo.Substring(16, 2), 16).ToString();
                        //    string second = Convert.ToInt32(variableHeadInfo.Substring(18, 2), 16).ToString();
                        //    DateTime datatime = new DateTime(int.Parse(year), int.Parse(month), int.Parse(day), int.Parse(hour), int.Parse(minute),int.Parse(second));

                        //    //5.流速数据
                        //    string speedInfo = temp.Substring(114 + 118 + 112 + setHeadInfoLength, speedLayers * 16 + 4);



                        //    //6.相关系数
                        //    string coefficientInfo = temp.Substring(114 + 118 + 112 + setHeadInfoLength + speedLayers * 16 + 4, speedLayers * 8 + 4);

                        //    //7.回波强度
                        //    string echoIntensityInfo = temp.Substring(114 + 118 + 112 + setHeadInfoLength + speedLayers * 16 + 4 + speedLayers * 8 + 4, speedLayers * 8 + 4);

                        //    //5.1 6.1 7.1 解析获得每层的数据
                        //    List<CentityLayerSpeed> layerInfoList = new List<CentityLayerSpeed>();
                        //    int speedFlag = 4;
                        //    int cfctFlag = 4;
                        //    int echoFlag = 4;
                        //    for(int layer=0;i< speedLayers; layer++)
                        //    {
                        //        speedFlag = layer * 16 + 4;
                        //        cfctFlag = layer * 8 + 4;
                        //        cfctFlag = layer * 8 + 4;
                        //        CentityLayerSpeed layerInfo = new CentityLayerSpeed();
                        //        try
                        //        {
                        //            layerInfo.layers = layer + 1;
                        //            layerInfo.StationID = "6000";
                        //            layerInfo.datatime = datatime;
                        //            layerInfo.speed1 = Convert.ToInt32(speedInfo.Substring(speedFlag + 2, 2) + speedInfo.Substring(speedFlag, 2), 16);
                        //            layerInfo.speed2 = Convert.ToInt32(speedInfo.Substring(speedFlag + 6, 2) + speedInfo.Substring(speedFlag+4, 2), 16);
                        //            layerInfo.speed3 = Convert.ToInt32(speedInfo.Substring(speedFlag + 10, 2) + speedInfo.Substring(speedFlag+8, 2), 16);
                        //            layerInfo.speed4 = Convert.ToInt32(speedInfo.Substring(speedFlag + 14, 2) + speedInfo.Substring(speedFlag+12, 2), 16);
                        //            layerInfo.cfct1 = Convert.ToInt32(coefficientInfo.Substring(cfctFlag, 2), 16);
                        //            layerInfo.cfct2 = Convert.ToInt32(coefficientInfo.Substring(cfctFlag+2, 2), 16);
                        //            layerInfo.cfct3 = Convert.ToInt32(coefficientInfo.Substring(cfctFlag+4, 2), 16);
                        //            layerInfo.cfct4 = Convert.ToInt32(coefficientInfo.Substring(cfctFlag+6, 2), 16);
                        //            layerInfo.echo1 = Convert.ToInt32(echoIntensityInfo.Substring(echoFlag, 2), 16);
                        //            layerInfo.echo2 = Convert.ToInt32(echoIntensityInfo.Substring(echoFlag+2, 2), 16);
                        //            layerInfo.echo3 = Convert.ToInt32(echoIntensityInfo.Substring(echoFlag+4, 2), 16);
                        //            layerInfo.echo4 = Convert.ToInt32(echoIntensityInfo.Substring(echoFlag+6, 2), 16);
                        //            layerInfoList.Add(layerInfo);
                        //        }
                        //        catch(Exception e)
                        //        {

                        //        }
                        //    }

                        //    //8.比例因子
                        //    string scaleFactorInfo = temp.Substring(114 + 118 + 112 + setHeadInfoLength + speedLayers * 16 + 4 + speedLayers * 8 + 4 + speedLayers * 8 + 4, 22);
                        //    //8.1 相关参数
                        //    List<CEntityAdcpParam> adcpParamList = new List<CEntityAdcpParam>();
                        //    CEntityAdcpParam adcpParam = new CEntityAdcpParam();
                        //    adcpParam.StationID = "6000";
                        //    adcpParam.datatime = datatime;
                        //    adcpParam.layers = speedLayers;
                        //    adcpParam.thickness = thickness;
                        //    adcpParam.blindzone = blindzone;
                        //    try
                        //    {
                        //        adcpParam.cfct = Convert.ToInt32(scaleFactorInfo.Substring(4, 2), 16);
                        //        adcpParam.voltage = Convert.ToInt32(scaleFactorInfo.Substring(6, 2), 16);
                        //        adcpParam.height1 = Convert.ToInt32(scaleFactorInfo.Substring(8, 2), 16);
                        //        adcpParam.height2 = Convert.ToInt32(scaleFactorInfo.Substring(10, 2), 16);
                        //        adcpParam.height3 = Convert.ToInt32(scaleFactorInfo.Substring(12, 2), 16);
                        //        adcpParam.height4 = Convert.ToInt32(scaleFactorInfo.Substring(14, 2), 16);
                        //        adcpParam.v1 = Convert.ToInt32(scaleFactorInfo.Substring(16, 2), 16);
                        //        adcpParam.v2 = Convert.ToInt32(scaleFactorInfo.Substring(18, 2), 16);
                        //        adcpParam.echo = Convert.ToInt32(scaleFactorInfo.Substring(20, 2), 16);
                        //        adcpParamList.Add(adcpParam);
                        //    }catch(Exception e)
                        //    {

                        //    }
                        #endregion



                        //调用计算dll
                        //TODO


                        //11.将数据写入返回数据结构
                        //CReportStruct report = new CReportStruct();
                        //report.Stationid = "6000";
                        //report.Type = "01";
                        //report.ReportType = EMessageType.EAdditional;
                        //report.StationType = EStationType.EHydrology;
                        //report.ChannelType = EChannelType.GPRS;
                        //report.ListenPort = this.GetListenPort().ToString();
                        //report.flagId = gprs;
                        //string rtype = report.ReportType == EMessageType.EAdditional ? "加报" : "定时报";
                        //List<CReportData> datas = new List<CReportData>();
                        //foreach(CentityLayerSpeed layerInfo in layerInfoList)
                        //{
                        //    CReportData data = new CReportData();
                        //    data.Time = layerInfo.datatime;
                        //    data.layer = layerInfo.layers;
                        //    data.speed1 = layerInfo.speed1;
                        //    data.speed2 = layerInfo.speed2;
                        //    data.speed3 = layerInfo.speed3;
                        //    data.speed4 = layerInfo.speed4;
                        //    data.cfct1 = layerInfo.cfct1;
                        //    data.cfct2 = layerInfo.cfct2;
                        //    data.cfct3 = layerInfo.cfct3;
                        //    data.cfct4 = layerInfo.cfct4;
                        //    data.echo1 = layerInfo.echo1;
                        //    data.echo2 = layerInfo.echo2;
                        //    data.echo3 = layerInfo.echo3;
                        //    data.echo4 = layerInfo.echo4;
                        //    datas.Add(data);
                        //}

                        //foreach(CEntityAdcpParam param in adcpParamList)
                        //{
                        //    CReportData data = new CReportData();
                        //    data.Time = param.datatime;
                        //    data.layers = param.layers;
                        //    data.thickness = param.thickness;
                        //    data.blindzone = param.blindzone;
                        //    data.cfct   = param.cfct;
                        //    data.voltage = param.voltage;
                        //    data.height1 = param.height1;
                        //    data.height2 = param.height2;
                        //    data.height3 = param.height3;
                        //    data.height4 = param.height4;
                        //    data.v1 = param.v1;
                        //    data.v2 = param.v2;
                        //    data.echo = param.echo;
                        //    datas.Add(data);
                        //}

                        //if (this.UpDataReceived != null)
                        //{
                        //    this.UpDataReceived.Invoke(null, new UpEventArgs() { Value = report, RawData = temp });
                        //}
                    }
                    catch (Exception e)
                    {
                        Debug.WriteLine("" + e.Message);
                    }
                }
            }
        }
        #region 接口函数
        public ushort GetListenPort()
        {
            return DTUdll.Instance.ListenPort;
        }

        public bool FindByID(string userID, out byte[] dtuID)
        {
            dtuID = null;
            List<HDModemInfoStruct> DTUList_1 = DTUList;
            //foreach (var item in DTUList_1)
            for (int i = 0; i < DTUList_1.Count; i++)
            {
                HDModemInfoStruct item = DTUList_1[i];
                if (System.Text.Encoding.Default.GetString(item.m_modemId).Substring(0, 11) == userID)
                {
                    dtuID = item.m_modemId;
                    return true;
                }
            }
            return false;
        }

        public void SendDataTwice(string id, string msg)
        {
            m_timer.Interval = 600;
            SendData(id, msg);
            if (s_isFirstSend)
            {
                MyMessage myMsg = new MyMessage() { ID = id, MSG = msg };
                s_isFirstSend = false;
                Thread t = new Thread(new ParameterizedThreadStart(ResendRead))
                {
                    Name = "重新发送读取线程",
                    IsBackground = true
                };
                t.Start(myMsg);
            }
        }

        public void SendDataTwiceForBatchTrans(string id, string msg)
        {
            m_timer.Interval = 60000;
            SendData(id, msg);
            if (s_isFirstSend)
            {
                MyMessage myMsg = new MyMessage() { ID = id, MSG = msg };
                s_isFirstSend = false;
                Thread t = new Thread(new ParameterizedThreadStart(ResendRead))
                {
                    Name = "重新发送读取线程",
                    IsBackground = true
                };
                t.Start(myMsg);
            }
        }

        #endregion

        #region 帮助函数
        public bool SendData(string id, string msg)
        {
            if (string.IsNullOrEmpty(msg))
            {
                return false;
            }
            //      Debug.WriteLine("GPRS发送数据:" + msg);
            InvokeMessage(msg, "发送");
            //      Debug.WriteLine("先停止计时器，然后在启动计时器");
            //  先停止计时器，然后在启动计时器
            m_timer.Stop();
            m_timer.Start();
            byte[] bmesg = System.Text.Encoding.Default.GetBytes(msg);
            if (DTUdll.Instance.SendHex(id, bmesg, (uint)bmesg.Length, null) == 0)
            {
                return true;
            }
            else
            {
                return false;
            }
        }

        private void ResendRead(object obj)
        {
            Debug.WriteLine(System.Threading.Thread.CurrentThread.Name + "休息1秒!");
            System.Threading.Thread.Sleep(1000);
            try
            {
                MyMessage myMsg = obj as MyMessage;
                if (null != myMsg)
                {
                    SendData(myMsg.ID, myMsg.MSG);
                }
            }
            catch (Exception exp) { Debug.WriteLine(exp.Message); }
            finally { s_isFirstSend = true; }
        }


        #endregion
    }
}
