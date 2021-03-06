﻿using DigitalPlatform;
using DigitalPlatform.RFID;
using DigitalPlatform.WPF;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace dp2SSL
{
    /// <summary>
    /// 和盘点有关的数据结构
    /// </summary>
    public static class InventoryData
    {
        // UID --> entity
        static Hashtable _entityTable = new Hashtable();

        public static Entity AddEntity(TagAndData tag, out bool isNewly)
        {
            if (_entityTable.ContainsKey(tag.OneTag.UID))
            {
                // TODO: 更新 tagInfo
                isNewly = false;
                Entity result = _entityTable[tag.OneTag.UID] as Entity;
                InventoryData.NewEntity(tag, result, false);
                return result;
            }

            var entity = InventoryData.NewEntity(tag, null, false);
            _entityTable[entity.UID] = entity;
            isNewly = true;
            return entity;
        }

        // 注：所创建的 Entity 对象其 Error 成员可能有值，表示有出错信息
        // Exception:
        //      可能会抛出异常 ArgumentException
        static Entity NewEntity(TagAndData tag, 
            Entity entity,
            bool throw_exception = true)
        {
            Entity result = entity;
            if (result == null)
            {
                result = new Entity
                {
                    UID = tag.OneTag.UID,
                    ReaderName = tag.OneTag.ReaderName,
                    Antenna = tag.OneTag.AntennaID.ToString(),
                    TagInfo = tag.OneTag.TagInfo,
                };
            }

            LogicChip chip = null;
            if (string.IsNullOrEmpty(tag.Type))
            {
                // Exception:
                //      可能会抛出异常 ArgumentException TagDataException
                try
                {
                    SetTagType(tag, out string pii, out chip);
                    result.PII = pii;
                }
                catch (Exception ex)
                {
                    App.CurrentApp.SpeakSequence("警告: 标签解析出错");
                    if (throw_exception == false)
                    {
                        result.AppendError($"RFID 标签格式错误: {ex.Message}",
                            "red",
                            "parseTagError");
                    }
                    else
                        throw ex;
                }
            }

#if NO
            // Exception:
            //      可能会抛出异常 ArgumentException 
            EntityCollection.SetPII(result, pii);
#endif

            // 2020/4/9
            if (tag.Type == "patron")
            {
                // 避免被当作图书同步到 dp2library
                result.PII = "(读者卡)" + result.PII;
                result.AppendError("读者卡误放入书柜", "red", "patronCard");
            }

            // 2020/7/15
            // 获得图书 RFID 标签的 OI 和 AOI 字段
            if (tag.Type == "book")
            {
                if (chip == null)
                {
                    // Exception:
                    //      可能会抛出异常 ArgumentException TagDataException
                    chip = LogicChip.From(tag.OneTag.TagInfo.Bytes,
            (int)tag.OneTag.TagInfo.BlockSize,
            "" // tag.TagInfo.LockStatus
            );
                }

                string oi = chip.FindElement(ElementOID.OI)?.Text;
                string aoi = chip.FindElement(ElementOID.AOI)?.Text;

                result.OI = oi;
                result.AOI = aoi;

                // 2020/8/27
                // 严格要求必须有 OI(AOI) 字段
                if (string.IsNullOrEmpty(oi) && string.IsNullOrEmpty(aoi))
                    result.AppendError("没有 OI 或 AOI 字段", "red", "missingOI");
            }
            return result;
        }

        // Exception:
        //      可能会抛出异常 ArgumentException TagDataException
        static void SetTagType(TagAndData data,
            out string pii,
            out LogicChip chip)
        {
            pii = null;
            chip = null;

            if (data.OneTag.Protocol == InventoryInfo.ISO14443A)
            {
                data.Type = "patron";
                return;
            }

            if (data.OneTag.TagInfo == null)
            {
                data.Type = ""; // 表示类型不确定
                return;
            }

            if (string.IsNullOrEmpty(data.Type))
            {
                // Exception:
                //      可能会抛出异常 ArgumentException TagDataException
                chip = LogicChip.From(data.OneTag.TagInfo.Bytes,
        (int)data.OneTag.TagInfo.BlockSize,
        "" // tag.TagInfo.LockStatus
        );
                pii = chip.FindElement(ElementOID.PII)?.Text;

                var typeOfUsage = chip.FindElement(ElementOID.TypeOfUsage)?.Text;
                if (typeOfUsage != null && typeOfUsage.StartsWith("8"))
                    data.Type = "patron";
                else
                    data.Type = "book";
            }
        }


        // Entity 附加的处理信息
        public class ProcessInfo
        {
            // 状态
            public string State { get; set; }
        }

        #region 处理列表

        static List<Entity> _entityList = new List<Entity>();
        static object _entityListSyncRoot = new object();

        // 复制列表
        public static List<Entity> CopyList()
        {
            lock (_entityListSyncRoot)
            {
                return new List<Entity>(_entityList);
            }
        }

        // 追加元素
        public static void AppendList(Entity entity)
        {
            lock (_entityListSyncRoot)
            {
                _entityList.Add(entity);
            }
        }

        public static void RemoveList(List<Entity> entities)
        {
            lock (_entityListSyncRoot)
            {
                foreach (var entity in entities)
                {
                    _entityList.Remove(entity);
                }
            }
        }

        #endregion

        #region 后台任务

        static Task _inventoryTask = null;

        // 监控间隔时间
        static TimeSpan _inventoryIdleLength = TimeSpan.FromSeconds(10);

        static AutoResetEvent _eventInventory = new AutoResetEvent(false);

        // 激活任务
        public static void ActivateInventory()
        {
            _eventInventory.Set();
        }

        // 启动盘点后台任务
        public static void StartInventoryTask()
        {
            if (_inventoryTask != null)
                return;

            CancellationToken token = App.CancelToken;

            token.Register(() =>
            {
                _eventInventory.Set();
            });

            _inventoryTask = Task.Factory.StartNew(async () =>
            {
                WpfClientInfo.WriteInfoLog("盘点后台线程开始");
                try
                {
                    while (token.IsCancellationRequested == false)
                    {
                        // await Task.Delay(TimeSpan.FromSeconds(10));
                        _eventInventory.WaitOne(_inventoryIdleLength);

                        token.ThrowIfCancellationRequested();

                        //
                        await ProcessingAsync();
                    }
                    _inventoryTask = null;
                }
                catch (OperationCanceledException)
                {

                }
                catch (Exception ex)
                {
                    WpfClientInfo.WriteErrorLog($"盘点后台线程出现异常: {ExceptionUtil.GetDebugText(ex)}");
                    App.SetError("inventory_worker", $"盘点后台线程出现异常: {ex.Message}");
                }
                finally
                {
                    WpfClientInfo.WriteInfoLog("盘点后台线程结束");
                }
            },
token,
TaskCreationOptions.LongRunning,
TaskScheduler.Default);
        }

        static async Task ProcessingAsync()
        {
            var list = CopyList();
            foreach(var entity in list)
            {
                // 获得册记录和书目摘要
                // .Value
                //      -1  出错
                //      0   没有找到
                //      1   找到
                var result = await LibraryChannelUtil.GetEntityDataAsync(entity.PII, "network");
                if (result.Value == -1)
                    entity.AppendError(result.ErrorInfo, "red", result.ErrorCode);
                else
                {
                    if (string.IsNullOrEmpty(result.Title) == false)
                        entity.Title = PageBorrow.GetCaption(result.Title);
                    if (string.IsNullOrEmpty(result.ItemXml) == false)
                        entity.SetData(result.ItemRecPath, result.ItemXml);
                }
            }

            // 把处理过的 entity 从 list 中移走
            RemoveList(list);
        }

        #endregion
    }
}
