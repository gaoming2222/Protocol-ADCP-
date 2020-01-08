/************************************************************************************
* Copyright (c) 2020 All Rights Reserved.
*命名空间：Protocol.Channel.HDGprs
*文件名： Write2File
*创建人： XXX
*创建时间：2020-1-5 10:50:33
*描述
*=====================================================================
*修改标记
*修改时间：2020-1-5 10:50:33
*修改人：XXX
*描述：
************************************************************************************/
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace Protocol.Channel.HDGprs
{
    public class Write2File
    {
        // 信息列表的互斥量
        private Mutex m_mutexListInfo;

        // 文件的互斥量
        private Mutex m_mutexWriteToFile;

        // 文件夹名
        private string m_strLogFileName;
        public Write2File(string filename)
        {
            //初始化互斥量
            m_mutexListInfo = new Mutex();
            m_mutexWriteToFile = new Mutex();

            m_strLogFileName = filename;
        }
        // 写入文件
        public void WriteInfoToFile(Object Objectstr)
        {
            string str = Objectstr.ToString();
            try
            {
                m_mutexWriteToFile.WaitOne();
                // 判断log文件夹是否存在
                if (!Directory.Exists(m_strLogFileName))
                {
                    // 创建文件夹
                    Directory.CreateDirectory(m_strLogFileName);
                }
                //string filename = "ReceivedLog" + DateTime.Now.ToString("yyyyMMdd") + ".log";
                string path = m_strLogFileName + "/" + "adcp.dt0";
                if (!File.Exists(path))
                {
                    // 不存在文件，新建一个
                    FileStream fs = new FileStream(path, FileMode.Create);
                    StreamWriter sw = new StreamWriter(fs);
                    //foreach (CTextInfo info in listInfo)
                    //{
                    //开始写入
                    sw.WriteLine(str);
                    //}
                    //清空缓冲区
                    sw.Flush();
                    //关闭流
                    sw.Close();
                    fs.Close();
                }
                else
                {
                    // 添加到现有文件
                    FileStream fs = new FileStream(path, FileMode.Append);
                    StreamWriter sw = new StreamWriter(fs);
                    //开始写入
                    //foreach (CTextInfo info in listInfo)
                    //{
                    //开始写入
                    sw.WriteLine(str);
                    //}
                    //清空缓冲区
                    sw.Flush();
                    //关闭流
                    sw.Close();
                    fs.Close();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.ToString());
            }
            finally
            {
                m_mutexWriteToFile.ReleaseMutex();
            }


        }
    }
}