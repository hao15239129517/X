﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using NewLife.Data;
using NewLife.Log;
using NewLife.Reflection;
using NewLife.Serialization;
using NewLife.Xml;
using XCode.Common;
using XCode.Configuration;
using XCode.DataAccessLayer;
using XCode.Model;

namespace XCode
{
    /// <summary>数据实体类基类。所有数据实体类都必须继承该类。</summary>
    [Serializable]
    public partial class Entity<TEntity> : EntityBase where TEntity : Entity<TEntity>, new()
    {
        #region 构造函数
        /// <summary>静态构造</summary>
        static Entity()
        {
            DAL.InitLog();
            //DAL.WriteDebugLog("开始初始化实体类{0}", typeof(TEntity).Name);

            EntityFactory.Register(typeof(TEntity), new EntityOperate());

            // 1，可以初始化该实体类型的操作工厂
            // 2，CreateOperate将会实例化一个TEntity对象，从而引发TEntity的静态构造函数，
            // 避免实际应用中，直接调用Entity的静态方法时，没有引发TEntity的静态构造函数。
            var entity = new TEntity();

            ////! 大石头 2011-03-14 以下过程改为异步处理
            ////  已确认，当实体类静态构造函数中使用了EntityFactory.CreateOperate(Type)方法时，可能出现死锁。
            ////  因为两者都会争夺EntityFactory中的op_cache，而CreateOperate(Type)拿到op_cache后，还需要等待当前静态构造函数执行完成。
            ////  不确定这样子是否带来后遗症
            //ThreadPool.QueueUserWorkItem(delegate
            //{
            //    EntityFactory.CreateOperate(typeof(TEntity), entity);
            //});

            //DAL.WriteDebugLog("完成初始化实体类{0}", typeof(TEntity).Name);
        }

        /// <summary>创建实体。</summary>
        /// <remarks>
        /// 可以重写改方法以实现实体对象的一些初始化工作。
        /// 切记，写为实例方法仅仅是为了方便重载，所要返回的实例绝对不会是当前实例。
        /// </remarks>
        /// <param name="forEdit">是否为了编辑而创建，如果是，可以再次做一些相关的初始化工作</param>
        /// <returns></returns>
        //[Obsolete("=>IEntityOperate")]
        [EditorBrowsable(EditorBrowsableState.Advanced)]
        protected virtual TEntity CreateInstance(Boolean forEdit = false)
        {
            //return new TEntity();
            // new TEntity会被编译为Activator.CreateInstance<TEntity>()，还不如Activator.CreateInstance()呢
            // Activator.CreateInstance()有缓存功能，而泛型的那个没有
            //return Activator.CreateInstance(typeof(TEntity)) as TEntity;
            var entity = typeof(TEntity).CreateInstance() as TEntity;
            Meta._Modules.Create(entity, forEdit);

            return entity;
        }
        #endregion

        #region 填充数据
        /// <summary>加载记录集。无数据时返回空集合而不是null。</summary>
        /// <param name="ds">记录集</param>
        /// <returns>实体数组</returns>
        public static IList<TEntity> LoadData(DataSet ds)
        {
            if (ds == null || ds.Tables.Count < 1) return new List<TEntity>();

            return LoadData(ds.Tables[0]);
        }

        /// <summary>加载数据表。无数据时返回空集合而不是null。</summary>
        /// <param name="dt">数据表</param>
        /// <returns>实体数组</returns>
        public static IList<TEntity> LoadData(DataTable dt)
        {
            if (dt == null) return new List<TEntity>();

            var list = DreAccessor.LoadData<TEntity>(dt);
            // 设置默认累加字段
            EntityAddition.SetField(list.Cast<IEntity>().ToList());
            foreach (var entity in list)
            {
                entity.OnLoad();
            }

            // 如果正在使用单对象缓存，则批量进入
            var sc = Meta.SingleCache;
            if (sc.Using)
            {
                // 查询列表异步加入对象缓存
                ThreadPool.UnsafeQueueUserWorkItem(s =>
                {
                    try
                    {
                        foreach (var item in list)
                        {
                            sc.Add(item);
                        }
                    }
                    catch (Exception ex)
                    {
                        XTrace.WriteException(ex);
                    }
                }, null);
            }

            return list;
        }

        private static IDataRowEntityAccessor DreAccessor { get { return XCodeService.CreateDataRowEntityAccessor(typeof(TEntity)); } }
        #endregion

        #region 操作
        private static IEntityPersistence Persistence { get { return XCodeService.Container.ResolveInstance<IEntityPersistence>(); } }

        /// <summary>插入数据，<see cref="Valid"/>后，在事务中调用<see cref="OnInsert"/>。</summary>
        /// <returns></returns>
        public override Int32 Insert() { return DoAction(OnInsert, true); }

        /// <summary>把该对象持久化到数据库，添加/更新实体缓存。</summary>
        /// <returns></returns>
        protected virtual Int32 OnInsert()
        {
            var rs = Meta.Session.Insert(this);
            // 设置默认累加字段
            EntityAddition.SetField(this);

            return rs;
        }

        /// <summary>更新数据，<see cref="Valid"/>后，在事务中调用<see cref="OnUpdate"/>。</summary>
        /// <returns></returns>
        public override Int32 Update() { return DoAction(OnUpdate, false); }

        /// <summary>更新数据库，同时更新实体缓存</summary>
        /// <returns></returns>
        protected virtual Int32 OnUpdate() { return Meta.Session.Update(this); }

        /// <summary>删除数据，通过在事务中调用OnDelete实现。</summary>
        /// <remarks>
        /// 删除时，如果有且仅有主键有脏数据，则可能是ObjectDataSource之类的删除操作。
        /// 该情况下，实体类没有完整的信息（仅有主键信息），将会导致无法通过扩展属性删除附属数据。
        /// 如果需要避开该机制，请清空脏数据。
        /// </remarks>
        /// <returns></returns>
        public override Int32 Delete()
        {
            if (HasDirty)
            {
                // 是否有且仅有主键有脏数据
                var names = Meta.Table.PrimaryKeys.Select(f => f.Name).OrderBy(k => k).ToArray();
                // 脏数据里面是否存在非主键且为true的
                var names2 = Dirtys.Where(d => d.Value).Select(d => d.Key).OrderBy(k => k).ToArray();
                // 序列相等，符合条件
                if (names.SequenceEqual(names2))
                {
                    // 再次查询
                    var entity = Find(Persistence.GetPrimaryCondition(this));
                    // 如果目标数据不存在，就没必要删除了
                    if (entity == null) return 0;

                    // 复制脏数据和扩展数据
                    foreach (var item in names)
                    {
                        entity.Dirtys[item] = true;
                    }
                    Extends.CopyTo(entity.Extends);

                    return entity.DoAction(OnDelete, null);
                }
            }

            return DoAction(OnDelete, null);
        }

        /// <summary>从数据库中删除该对象，同时从实体缓存中删除</summary>
        /// <returns></returns>
        protected virtual Int32 OnDelete() { return Meta.Session.Delete(this); }

        Int32 DoAction(Func<Int32> func, Boolean? isnew)
        {
            if (Meta.Table.DataTable.InsertOnly)
            {
                if (isnew == null) throw new XCodeException("只写的日志型数据禁止删除！");
                if (!isnew.Value) throw new XCodeException("只写的日志型数据禁止修改！");
            }

            //using (var trans = new EntityTransaction<TEntity>())
            //{
            if (enableValid)
            {
                var rt = false;
                if (isnew != null)
                {
                    Valid(isnew.Value);
                    rt = Meta._Modules.Valid(this, isnew.Value);
                }
                else
                    rt = Meta._Modules.Delete(this);

                // 没有更新任何数据
                if (!rt) return -1;
            }

            var rs = func();

            //trans.Commit();

            return rs;
            //}
        }

        /// <summary>保存。根据主键检查数据库中是否已存在该对象，再决定调用Insert或Update</summary>
        /// <returns></returns>
        public override Int32 Save()
        {
            // 优先使用自增字段判断
            var fi = Meta.Table.Identity;
            if (fi != null) return Convert.ToInt64(this[fi.Name]) > 0 ? Update() : Insert();

            // 如果唯一主键不为空，应该通过后面判断，而不是直接Update
            if (IsNullKey) return Insert();

            // 来自数据库直接Update
            if (_IsFromDatabase)
            {
                //var uq = Meta.Unique;
                //if (uq != null && !Dirtys[uq.Name]) return Update();
                var pks = Meta.Table.PrimaryKeys;
                if (pks.Length > 0 && pks.All(e => !Dirtys[e.Name])) return Update();
            }

            return FindCount(Persistence.GetPrimaryCondition(this), null, null, 0, 0) > 0 ? Update() : Insert();
        }

        /// <summary>不需要验证的保存，不执行Valid，一般用于快速导入数据</summary>
        /// <returns></returns>
        public override Int32 SaveWithoutValid()
        {
            enableValid = false;
            try { return Save(); }
            finally { enableValid = true; }
        }

        /// <summary>异步保存。实现延迟保存，大事务保存。主要面向日志表和频繁更新的在线记录表</summary>
        /// <param name="msDelay">延迟保存的时间。默认0ms近实时保存</param>
        /// <remarks>
        /// 调用平均耗时190.86ns，IPModule占38.89%，TimeModule占16.31%，UserModule占7.20%，Valid占14.36%
        /// </remarks>
        /// <returns>是否成功加入异步队列，实体对象已存在于队列中则返回false</returns>
        public override Boolean SaveAsync(Int32 msDelay = 0)
        {
            var isnew = false;

            // 优先使用自增字段判断
            var fi = Meta.Table.Identity;
            if (fi != null)
                isnew = Convert.ToInt64(this[fi.Name]) == 0;
            // 如果唯一主键不为空，应该通过后面判断，而不是直接Update
            else if (IsNullKey)
                isnew = true;

            // 提前执行Valid，让它提前准备好验证数据
            if (enableValid)
            {
                Valid(isnew);
                Meta._Modules.Valid(this, isnew);
            }

            if (!HasDirty) return false;

            return Meta.Session.Dal.Queue.Add(this, msDelay);
        }

        [NonSerialized]
        Boolean enableValid = true;

        /// <summary>验证数据，通过抛出异常的方式提示验证失败。</summary>
        /// <remarks>建议重写者调用基类的实现，因为基类根据数据字段的唯一索引进行数据验证。</remarks>
        /// <param name="isNew">是否新数据</param>
        public virtual void Valid(Boolean isNew)
        {
            //// 实体来自数据库时，不要对唯一索引进行校验
            //if (_IsFromDatabase) return;

            //// 根据索引，判断唯一性
            //var table = Meta.Table.DataTable;
            //var dis = table.Indexes;
            //if (dis != null && dis.Count > 0)
            //{
            //    // 遍历所有索引
            //    foreach (var item in dis)
            //    {
            //        // 只处理唯一索引
            //        if (!item.Unique) continue;

            //        // 需要转为别名，也就是字段名
            //        var columns = table.GetColumns(item.Columns);
            //        if (columns == null || columns.Length < 1) continue;

            //        // 不处理自增
            //        if (columns.All(c => c.Identity)) continue;

            //        // 记录字段是否有更新
            //        var changed = false;
            //        if (!isNew) changed = columns.Any(c => Dirtys[c.Name]);

            //        // 存在检查
            //        if (isNew || changed) CheckExist(isNew, columns.Select(c => c.Name).Distinct().ToArray());
            //    }
            //}
        }

        /// <summary>根据指定键检查数据，返回数据是否已存在</summary>
        /// <param name="names"></param>
        /// <returns></returns>
        public virtual Boolean Exist(params String[] names) { return Exist(true, names); }

        /// <summary>根据指定键检查数据是否已存在，若已存在，抛出ArgumentOutOfRangeException异常</summary>
        /// <param name="names"></param>
        public virtual void CheckExist(params String[] names) { CheckExist(true, names); }

        /// <summary>根据指定键检查数据是否已存在，若已存在，抛出ArgumentOutOfRangeException异常</summary>
        /// <param name="isNew">是否新数据</param>
        /// <param name="names"></param>
        public virtual void CheckExist(Boolean isNew, params String[] names)
        {
            if (Exist(isNew, names))
            {
                var sb = new StringBuilder();
                String name = null;
                for (var i = 0; i < names.Length; i++)
                {
                    if (sb.Length > 0) sb.Append("，");

                    FieldItem field = Meta.Table.FindByName(names[i]);
                    if (field != null) name = field.Description;
                    if (String.IsNullOrEmpty(name)) name = names[i];

                    sb.AppendFormat("{0}={1}", name, this[names[i]]);
                }

                name = Meta.Table.Description;
                if (String.IsNullOrEmpty(name)) name = typeof(TEntity).Name;
                sb.AppendFormat(" 的{0}已存在！", name);

                throw new ArgumentOutOfRangeException(String.Join(",", names), this[names[0]], sb.ToString());
            }
        }

        /// <summary>根据指定键检查数据，返回数据是否已存在</summary>
        /// <param name="isNew">是否新数据</param>
        /// <param name="names"></param>
        /// <returns></returns>
        public virtual Boolean Exist(Boolean isNew, params String[] names)
        {
            // 根据指定键查找所有符合的数据，然后比对。
            // 当然，也可以通过指定键和主键配合，找到拥有指定键，但是不是当前主键的数据，只查记录数。
            var values = new Object[names.Length];
            for (var i = 0; i < names.Length; i++)
            {
                values[i] = this[names[i]];
            }

            var field = Meta.Unique;
            var val = this[field.Name];
            var cache = Meta.Session.Cache;
            if (!cache.Using)
            {
                //// 如果是空主键，则采用直接判断记录数的方式，以加快速度
                //if (IsNullKey) return FindCount(names, values) > 0;

                var exp = new WhereExpression();
                for (var i = 0; i < names.Length; i++)
                {
                    var fi = Meta.Table.FindByName(names[i]);
                    exp &= fi == values[i];
                }

                var list = FindAll(exp, null, null, 0, 0);
                if (list == null || list.Count < 1) return false;
                if (list.Count > 1) return true;

                // 如果是Guid等主键，可能提前赋值，插入操作不能比较主键，直接判断判断存在的唯一索引即可
                if (isNew && !field.IsIdentity) return true;

                return !Object.Equals(val, list[0][field.Name]);
            }
            else
            {
                // 如果是空主键，则采用直接判断记录数的方式，以加快速度
                var list = cache.FindAll(e =>
                {
                    for (var i = 0; i < names.Length; i++)
                    {
                        if (e[names[i]] != values[i]) return false;
                    }
                    return true;
                });
                if (IsNullKey) return list.Count > 0;

                if (list == null || list.Count < 1) return false;
                if (list.Count > 1) return true;

                // 如果是Guid等主键，可能提前赋值，插入操作不能比较主键，直接判断判断存在的唯一索引即可
                if (isNew && !field.IsIdentity) return true;

                return !Object.Equals(val, list[0][field.Name]);
            }
        }
        #endregion

        #region 查找单个实体
        /// <summary>根据属性以及对应的值，查找单个实体</summary>
        /// <param name="name">属性名称</param>
        /// <param name="value">属性值</param>
        /// <returns></returns>
        public static TEntity Find(String name, Object value) { return Find(new String[] { name }, new Object[] { value }); }

        /// <summary>根据属性列表以及对应的值列表，查找单个实体</summary>
        /// <param name="names">属性名称集合</param>
        /// <param name="values">属性值集合</param>
        /// <returns></returns>
        public static TEntity Find(String[] names, Object[] values)
        {
            var exp = new WhereExpression();
            // 判断自增和主键
            if (names != null && names.Length == 1)
            {
                var field = Meta.Table.FindByName(names[0]);
                if ((field as FieldItem) != null && (field.IsIdentity || field.PrimaryKey))
                {
                    // 唯一键为自增且参数小于等于0时，返回空
                    if (Helper.IsNullKey(values[0], field.Type)) return null;

                    exp &= field == values[0];
                    return FindUnique(exp);
                }
            }

            for (var i = 0; i < names.Length; i++)
            {
                var fi = Meta.Table.FindByName(names[i]);
                exp &= fi == values[i];
            }

            // 判断唯一索引，唯一索引也不需要分页
            var di = Meta.Table.DataTable.GetIndex(names);
            if (di != null && di.Unique) return FindUnique(exp);

            return Find(exp);
        }

        /// <summary>根据条件查找唯一的单个实体</summary>
        /// 根据条件查找唯一的单个实体，因为是唯一的，所以不需要分页和排序。
        /// 如果不确定是否唯一，一定不要调用该方法，否则会返回大量的数据。
        /// <remarks>
        /// </remarks>
        /// <param name="where">查询条件</param>
        /// <returns></returns>
        static TEntity FindUnique(Expression where)
        {
            var session = Meta.Session;
            var ps = session.Dal.Db.UserParameter ? new Dictionary<String, Object>() : null;
            var wh = where?.GetString(ps);

            var builder = new SelectBuilder
            {
                Table = session.FormatedTableName,
                // 谨记：某些项目中可能在where中使用了GroupBy，在分页时可能报错
                Where = wh
            };

            // 提取参数
            builder = FixParam(builder, ps);

            var list = LoadData(session.Query(builder, 0, 0));
            if (list == null || list.Count < 1) return null;

            if (list.Count > 1 && DAL.Debug)
            {
                DAL.WriteLog("调用FindUnique(\"{0}\")不合理，只有返回唯一记录的查询条件才允许调用！", wh);
#if !__CORE__
                XTrace.DebugStack(5);
#endif
            }
            return list[0];
        }

        /// <summary>根据条件查找单个实体</summary>
        /// <param name="where">查询条件</param>
        /// <returns></returns>
        public static TEntity Find(Expression where)
        {
            var list = FindAll(where, null, null, 0, 1);
            return list.Count < 1 ? null : list[0];
        }

        /// <summary>根据主键查找单个实体</summary>
        /// <param name="key">唯一主键的值</param>
        /// <returns></returns>
        public static TEntity FindByKey(Object key)
        {
            var field = Meta.Unique;
            if (field == null) throw new ArgumentNullException("Meta.Unique", "FindByKey方法要求" + typeof(TEntity).FullName + "有唯一主键！");

            // 唯一键为自增且参数小于等于0时，返回空
            if (Helper.IsNullKey(key, field.Type)) return null;

            return Find(field.Name, key);
        }

        /// <summary>根据主键查询一个实体对象用于表单编辑</summary>
        /// <param name="key">唯一主键的值</param>
        /// <returns></returns>
        public static TEntity FindByKeyForEdit(Object key)
        {
            var field = Meta.Unique;
            if (field == null) throw new ArgumentNullException("Meta.Unique", "FindByKeyForEdit方法要求该表有唯一主键！");

            // 参数为空时，返回新实例
            if (key == null)
            {
                //IEntityOperate factory = EntityFactory.CreateOperate(typeof(TEntity));
                return Meta.Factory.Create(true) as TEntity;
            }

            var type = field.Type;

            // 唯一键为自增且参数小于等于0时，返回新实例
            if (Helper.IsNullKey(key, type))
            {
                if (type.IsInt() && !field.IsIdentity && DAL.Debug) DAL.WriteLog("{0}的{1}字段是整型主键，你是否忘记了设置自增？", Meta.TableName, field.ColumnName);

                return Meta.Factory.Create(true) as TEntity;
            }

            // 此外，一律返回 查找值，即使可能是空。而绝不能在找不到数据的情况下给它返回空，因为可能是找不到数据而已，而返回新实例会导致前端以为这里是新增数据
            var entity = Find(field.Name, key);

            // 判断实体
            if (entity == null)
            {
                String msg = null;
                if (Helper.IsNullKey(key, field.Type))
                    msg = String.Format("参数错误！无法取得编号为{0}的{1}！可能未设置自增主键！", key, Meta.Table.Description);
                else
                    msg = String.Format("参数错误！无法取得编号为{0}的{1}！", key, Meta.Table.Description);

                throw new XCodeException(msg);
            }

            return entity;
        }

        /// <summary>查询指定字段的最小值</summary>
        /// <param name="field">指定字段</param>
        /// <param name="where">条件字句</param>
        /// <returns></returns>
        public static Int32 FindMin(String field, Expression where = null)
        {
            var fd = Meta.Table.FindByName(field);
            var list = FindAll(where, fd, null, 0, 1);
            return list.Count < 1 ? 0 : Convert.ToInt32(list[0][fd.Name]);
        }

        /// <summary>查询指定字段的最大值</summary>
        /// <param name="field">指定字段</param>
        /// <param name="where">条件字句</param>
        /// <returns></returns>
        public static Int32 FindMax(String field, Expression where = null)
        {
            var fd = Meta.Table.FindByName(field);
            var list = FindAll(where, fd.Desc(), null, 0, 1);
            return list.Count < 1 ? 0 : Convert.ToInt32(list[0][fd.Name]);
        }
        #endregion

        #region 静态查询
        /// <summary>获取所有数据。获取大量数据时会非常慢，慎用。没有数据时返回空集合而不是null</summary>
        /// <returns>实体数组</returns>
        public static IList<TEntity> FindAll() { return FindAll("", null, null, 0, 0); }

        /// <summary>根据名称获取数据集。没有数据时返回空集合而不是null</summary>
        /// <param name="name"></param>
        /// <param name="value"></param>
        /// <returns></returns>
        public static IList<TEntity> FindAll(String name, Object value) { return FindAll(new String[] { name }, new Object[] { value }); }

        /// <summary>根据属性列表以及对应的值列表，查找单个实体</summary>
        /// <param name="names">属性名称集合</param>
        /// <param name="values">属性值集合</param>
        /// <returns></returns>
        public static IList<TEntity> FindAll(String[] names, Object[] values)
        {
            var exp = new WhereExpression();

            for (var i = 0; i < names.Length; i++)
            {
                var fi = Meta.Table.FindByName(names[i]);
                exp &= fi == values[i];
            }

            return FindAll(exp, null, null, 0, 0);
        }

        /// <summary>最标准的查询数据。没有数据时返回空集合而不是null</summary>
        /// <remarks>
        /// 最经典的批量查询，看这个Select @selects From Table Where @where Order By @order Limit @startRowIndex,@maximumRows，你就明白各参数的意思了。
        /// </remarks>
        /// <param name="where">条件字句，不带Where</param>
        /// <param name="order">排序字句，不带Order By</param>
        /// <param name="selects">查询列，默认null表示所有字段</param>
        /// <param name="startRowIndex">开始行，0表示第一行</param>
        /// <param name="maximumRows">最大返回行数，0表示所有行</param>
        /// <returns>实体集</returns>
        public static IList<TEntity> FindAll(String where, String order, String selects, Int64 startRowIndex, Int64 maximumRows)
        {
            var session = Meta.Session;

            var builder = CreateBuilder(where, order, selects, startRowIndex, maximumRows);
            return LoadData(session.Query(builder, startRowIndex, maximumRows));
        }

        /// <summary>最标准的查询数据。没有数据时返回空集合而不是null</summary>
        /// <remarks>
        /// 最经典的批量查询，看这个Select @selects From Table Where @where Order By @order Limit @startRowIndex,@maximumRows，你就明白各参数的意思了。
        /// </remarks>
        /// <param name="where">条件字句，不带Where</param>
        /// <param name="order">排序字句，不带Order By</param>
        /// <param name="selects">查询列，默认null表示所有字段</param>
        /// <param name="startRowIndex">开始行，0表示第一行</param>
        /// <param name="maximumRows">最大返回行数，0表示所有行</param>
        /// <returns>实体集</returns>
        public static IList<TEntity> FindAll(Expression where, String order, String selects, Int64 startRowIndex, Int64 maximumRows)
        {
            var session = Meta.Session;

            #region 海量数据查询优化
            // 海量数据尾页查询优化
            // 在海量数据分页中，取越是后面页的数据越慢，可以考虑倒序的方式
            // 只有在百万数据，且开始行大于五十万时才使用

            // 如下优化，避免了每次都调用Meta.Count而导致形成一次查询，虽然这次查询时间损耗不大
            // 但是绝大多数查询，都不需要进行类似的海量数据优化，显然，这个startRowIndex将会挡住99%以上的浪费
            Int64 count = 0;
            if (startRowIndex > 500000 && (count = session.LongCount) > 1000000)
            {
                // 计算本次查询的结果行数
                if (!String.IsNullOrEmpty(where?.GetString(null))) count = FindCount(where, order, selects, startRowIndex, maximumRows);
                // 游标在中间偏后
                if (startRowIndex * 2 > count)
                {
                    var order2 = order;
                    var bk = false; // 是否跳过

                    #region 排序倒序
                    // 默认是自增字段的降序
                    var fi = Meta.Unique;
                    if (String.IsNullOrEmpty(order2) && fi != null && fi.IsIdentity) order2 = fi.Name + " Desc";

                    if (!String.IsNullOrEmpty(order2))
                    {
                        //2014-01-05 Modify by Apex
                        //处理order by带有函数的情况，避免分隔时将函数拆分导致错误
                        foreach (Match match in Regex.Matches(order2, @"\([^\)]*\)", RegexOptions.Singleline))
                        {
                            order2 = order2.Replace(match.Value, match.Value.Replace(",", "★"));
                        }
                        var ss = order2.Split(',');
                        var sb = new StringBuilder();
                        foreach (var item in ss)
                        {
                            var fn = item;
                            var od = "asc";

                            var p = fn.LastIndexOf(" ");
                            if (p > 0)
                            {
                                od = item.Substring(p).Trim().ToLower();
                                fn = item.Substring(0, p).Trim();
                            }

                            switch (od)
                            {
                                case "asc":
                                    od = "desc";
                                    break;
                                case "desc":
                                    //od = "asc";
                                    od = null;
                                    break;
                                default:
                                    bk = true;
                                    break;
                            }
                            if (bk) break;

                            if (sb.Length > 0) sb.Append(", ");
                            sb.AppendFormat("{0} {1}", fn, od);
                        }

                        order2 = sb.ToString().Replace("★", ",");
                    }
                    #endregion

                    // 没有排序的实在不适合这种办法，因为没办法倒序
                    if (!String.IsNullOrEmpty(order2))
                    {
                        // 最大可用行数改为实际最大可用行数
                        var max = (Int32)Math.Min(maximumRows, count - startRowIndex);
                        //if (max <= 0) return null;
                        if (max <= 0) return new List<TEntity>();
                        var start = (Int32)(count - (startRowIndex + maximumRows));

                        var builder2 = CreateBuilder(where, order2, selects, start, max);
                        var list = LoadData(session.Query(builder2, start, max));
                        if (list == null || list.Count < 1) return list;
                        // 因为这样取得的数据是倒过来的，所以这里需要再倒一次
                        list.Reverse();
                        return list;
                    }
                }
            }
            #endregion

            var builder = CreateBuilder(where, order, selects, startRowIndex, maximumRows);
            return LoadData(session.Query(builder, startRowIndex, maximumRows));
        }

        /// <summary>同时查询满足条件的记录集和记录总数。没有数据时返回空集合而不是null</summary>
        /// <param name="where">条件，不带Where</param>
        /// <param name="page">分页排序参数，同时返回满足条件的总记录数</param>
        /// <returns></returns>
        public static IList<TEntity> FindAll(Expression where, PageParameter page = null)
        {
            if (page == null) return FindAll(where, null, null, 0, 0);

            // 先查询满足条件的记录数，如果没有数据，则直接返回空集合，不再查询数据
            if (page.RetrieveTotalCount)
            {
                var rows = page.TotalCount = FindCount(where, null, null, 0, 0);
                if (rows <= 0) return new List<TEntity>();
            }

            // 验证排序字段，避免非法
            var orderby = page.OrderBy;
            if (!page.Sort.IsNullOrEmpty())
            {
                var st = Meta.Table.FindByName(page.Sort);
                page.Sort = st?.ColumnName;
                orderby = page.OrderBy;

                //!!! 恢复排序字段，否则属性名和字段名不一致时前台无法降序
                page.Sort = st?.Name;
            }

            // 采用起始行还是分页
            if (page.StartRow >= 0)
                return FindAll(where, orderby, null, page.StartRow, page.PageSize);
            else
                return FindAll(where, orderby, null, (page.PageIndex - 1) * page.PageSize, page.PageSize);
        }
        #endregion

        #region 缓存查询
        /// <summary>查找所有缓存。没有数据时返回空集合而不是null</summary>
        /// <returns></returns>
        public static IList<TEntity> FindAllWithCache() { return Meta.Session.Cache.Entities; }
        #endregion

        #region 取总记录数
        /// <summary>返回总记录数</summary>
        /// <returns></returns>
        public static Int64 FindCount() { return FindCount("", null, null, 0, 0); }

        /// <summary>返回总记录数</summary>
        /// <param name="where">条件，不带Where</param>
        /// <param name="order">排序，不带Order By。这里无意义，仅仅为了保持与FindAll相同的方法签名</param>
        /// <param name="selects">查询列。这里无意义，仅仅为了保持与FindAll相同的方法签名</param>
        /// <param name="startRowIndex">开始行，0表示第一行。这里无意义，仅仅为了保持与FindAll相同的方法签名</param>
        /// <param name="maximumRows">最大返回行数，0表示所有行。这里无意义，仅仅为了保持与FindAll相同的方法签名</param>
        /// <returns>总行数</returns>
        public static Int32 FindCount(String where, String order = null, String selects = null, Int64 startRowIndex = 0, Int64 maximumRows = 0)
        {
            var session = Meta.Session;

            // 如果总记录数超过10万，为了提高性能，返回快速查找且带有缓存的总记录数
            if (String.IsNullOrEmpty(where) && session.LongCount > 100000) return session.Count;

            var sb = new SelectBuilder
            {
                Table = session.FormatedTableName,
                Where = where
            };

            // 分组查分组数的时候，必须带上全部selects字段
            if (!sb.GroupBy.IsNullOrEmpty()) sb.Column = selects;

            return session.QueryCount(sb);
        }

        /// <summary>返回总记录数</summary>
        /// <param name="where">条件，不带Where</param>
        /// <param name="order">排序，不带Order By。这里无意义，仅仅为了保持与FindAll相同的方法签名</param>
        /// <param name="selects">查询列。这里无意义，仅仅为了保持与FindAll相同的方法签名</param>
        /// <param name="startRowIndex">开始行，0表示第一行。这里无意义，仅仅为了保持与FindAll相同的方法签名</param>
        /// <param name="maximumRows">最大返回行数，0表示所有行。这里无意义，仅仅为了保持与FindAll相同的方法签名</param>
        /// <returns>总行数</returns>
        public static Int64 FindCount(Expression where, String order = null, String selects = null, Int64 startRowIndex = 0, Int64 maximumRows = 0)
        {
            var session = Meta.Session;
            var ps = session.Dal.Db.UserParameter ? new Dictionary<String, Object>() : null;
            var wh = where?.GetString(ps);

            // 如果总记录数超过10万，为了提高性能，返回快速查找且带有缓存的总记录数
            if (String.IsNullOrEmpty(wh) && session.LongCount > 100000) return session.LongCount;

            var builder = new SelectBuilder
            {
                Table = session.FormatedTableName,
                Where = wh
            };

            // 提取参数
            builder = FixParam(builder, ps);

            // 分组查分组数的时候，必须带上全部selects字段
            if (!builder.GroupBy.IsNullOrEmpty()) builder.Column = selects;

            return session.QueryCount(builder);
        }
        #endregion

        #region 获取查询SQL
        /// <summary>获取查询SQL。主要用于构造子查询</summary>
        /// <param name="where">条件，不带Where</param>
        /// <param name="order">排序，不带Order By</param>
        /// <param name="selects">查询列</param>
        /// <param name="startRowIndex">开始行，0表示第一行</param>
        /// <param name="maximumRows">最大返回行数，0表示所有行</param>
        /// <returns>实体集</returns>
        public static SelectBuilder FindSQL(String where, String order, String selects, Int32 startRowIndex = 0, Int32 maximumRows = 0)
        {
            var builder = CreateBuilder(where, order, selects, startRowIndex, maximumRows, false);
            return Meta.Session.PageSplit(builder, startRowIndex, maximumRows);
        }

        /// <summary>获取查询唯一键的SQL。比如Select ID From Table</summary>
        /// <param name="where"></param>
        /// <returns></returns>
        public static SelectBuilder FindSQLWithKey(String where = null)
        {
            var f = Meta.Unique;
            return FindSQL(where, null, f != null ? Meta.FormatName(f.ColumnName) : null, 0, 0);
        }
        #endregion

        #region 高级查询
        /// <summary>查询满足条件的记录集，分页、排序。没有数据时返回空集合而不是null</summary>
        /// <param name="key">关键字</param>
        /// <param name="order">排序，不带Order By</param>
        /// <param name="startRowIndex">开始行，0表示第一行</param>
        /// <param name="maximumRows">最大返回行数，0表示所有行</param>
        /// <returns>实体集</returns>
        [Obsolete("=>Search(DateTime start, DateTime end, String key, PageParameter param)")]
        public static IList<TEntity> Search(String key, String order, Int64 startRowIndex, Int64 maximumRows)
        {
            return FindAll(SearchWhereByKeys(key, null), order, null, startRowIndex, maximumRows);
        }

        /// <summary>查询满足条件的记录总数，分页和排序无效，带参数是因为ObjectDataSource要求它跟Search统一</summary>
        /// <param name="key">关键字</param>
        /// <param name="order">排序，不带Order By</param>
        /// <param name="startRowIndex">开始行，0表示第一行</param>
        /// <param name="maximumRows">最大返回行数，0表示所有行</param>
        /// <returns>记录数</returns>
        [Obsolete("=>Search(DateTime start, DateTime end, String key, PageParameter param)")]
        public static Int32 SearchCount(String key, String order, Int64 startRowIndex, Int64 maximumRows)
        {
            return (Int32)FindCount(SearchWhereByKeys(key, null), null, null, 0, 0);
        }

        /// <summary>同时查询满足条件的记录集和记录总数。没有数据时返回空集合而不是null</summary>
        /// <param name="key"></param>
        /// <param name="page">分页排序参数，同时返回满足条件的总记录数</param>
        /// <returns></returns>
        [Obsolete("=>Search(DateTime start, DateTime end, String key, PageParameter param)")]
        public static IList<TEntity> Search(String key, PageParameter page)
        {
            return FindAll(SearchWhereByKeys(key), page);
        }

        /// <summary>同时查询满足条件的记录集和记录总数。没有数据时返回空集合而不是null</summary>
        /// <param name="start">开始时间</param>
        /// <param name="end">结束时间</param>
        /// <param name="key">关键字</param>
        /// <param name="page">分页排序参数，同时返回满足条件的总记录数</param>
        /// <returns></returns>
        public static IList<TEntity> Search(DateTime start, DateTime end, String key, PageParameter page)
        {
            var df = Meta.Factory.Default as TEntity;
            return FindAll(df.SearchWhere(start, end, key, page), page);
        }

        /// <summary>构造高级查询条件</summary>
        /// <param name="start">开始时间</param>
        /// <param name="end">结束时间</param>
        /// <param name="key">关键字</param>
        /// <param name="page">分页排序参数，同时返回满足条件的总记录数</param>
        /// <returns></returns>
        protected virtual WhereExpression SearchWhere(DateTime start, DateTime end, String key, PageParameter page)
        {
            var exp = SearchWhereByKeys(key);

            if (start > DateTime.MinValue || end > DateTime.MinValue)
            {
                var fi = Meta.Factory.MasterTime;
                if (fi != null) exp &= fi.Between(start, end);
            }

            return exp;
        }

        /// <summary>根据空格分割的关键字集合构建查询条件</summary>
        /// <param name="keys">空格分割的关键字集合</param>
        /// <param name="fields">要查询的字段，默认为空表示查询所有字符串字段</param>
        /// <param name="func">处理每一个查询关键字的回调函数</param>
        /// <returns></returns>
        public static WhereExpression SearchWhereByKeys(String keys, FieldItem[] fields = null, Func<String, FieldItem[], WhereExpression> func = null)
        {
            var exp = new WhereExpression();
            if (String.IsNullOrEmpty(keys)) return exp;

            if (func == null) func = SearchWhereByKey;

            var ks = keys.Split(" ");

            for (var i = 0; i < ks.Length; i++)
            {
                if (!ks[i].IsNullOrWhiteSpace()) exp &= func(ks[i].Trim(), fields);
            }

            return exp;
        }

        /// <summary>构建关键字查询条件</summary>
        /// <param name="key">关键字</param>
        /// <param name="fields">要查询的字段，默认为空表示查询所有字符串字段</param>
        /// <returns></returns>
        public static WhereExpression SearchWhereByKey(String key, FieldItem[] fields = null)
        {
            var exp = new WhereExpression();
            if (key.IsNullOrEmpty()) return exp;

            if (fields == null || fields.Length == 0) fields = Meta.Fields;
            foreach (var item in fields)
            {
                if (item.Type != typeof(String)) continue;

                exp |= item.Contains(key);
            }

            return exp;
        }
        #endregion

        #region 静态操作
        /// <summary>把一个实体对象持久化到数据库</summary>
        /// <param name="obj">实体对象</param>
        /// <returns>返回受影响的行数</returns>
        [EditorBrowsable(EditorBrowsableState.Advanced)]
        public static Int32 Insert(TEntity obj) { return obj.Insert(); }

        /// <summary>把一个实体对象更新到数据库</summary>
        /// <param name="obj">实体对象</param>
        /// <returns>返回受影响的行数</returns>
        [EditorBrowsable(EditorBrowsableState.Advanced)]
        public static Int32 Update(TEntity obj) { return obj.Update(); }

        /// <summary>
        /// 从数据库中删除指定实体对象。
        /// 实体类应该实现该方法的另一个副本，以唯一键或主键作为参数
        /// </summary>
        /// <param name="obj">实体对象</param>
        /// <returns>返回受影响的行数，可用于判断被删除了多少行，从而知道操作是否成功</returns>
        [EditorBrowsable(EditorBrowsableState.Advanced)]
        public static Int32 Delete(TEntity obj) { return obj.Delete(); }

        /// <summary>把一个实体对象更新到数据库</summary>
        /// <param name="obj">实体对象</param>
        /// <returns>返回受影响的行数</returns>
        [EditorBrowsable(EditorBrowsableState.Advanced)]
        public static Int32 Save(TEntity obj) { return obj.Save(); }
        #endregion

        #region 构造SQL语句
        static SelectBuilder CreateBuilder(Expression where, String order, String selects, Int64 startRowIndex, Int64 maximumRows, Boolean needOrderByID = true)
        {
            var session = Meta.Session;
            var ps = session.Dal.Db.UserParameter ? new Dictionary<String, Object>() : null;
            var wh = where?.GetString(ps);
            var builder = CreateBuilder(wh, order, selects, startRowIndex, maximumRows, needOrderByID);

            builder = FixParam(builder, ps);

            return builder;
        }

        static SelectBuilder CreateBuilder(String where, String order, String selects, Int64 startRowIndex, Int64 maximumRows, Boolean needOrderByID = true)
        {
            var builder = new SelectBuilder
            {
                Column = selects,
                Table = Meta.Session.FormatedTableName,
                OrderBy = order,
                // 谨记：某些项目中可能在where中使用了GroupBy，在分页时可能报错
                Where = where
            };

            // XCode对于默认排序的规则：自增主键降序，其它情况默认
            // 返回所有记录
            if (!needOrderByID && startRowIndex <= 0 && maximumRows <= 0) return builder;

            var fi = Meta.Table.Identity;
            if (fi != null)
            {
                builder.Key = Meta.FormatName(fi.ColumnName);

                // 默认获取数据时，还是需要指定按照自增字段降序，符合使用习惯
                // 有GroupBy也不能加排序
                if (builder.OrderBy.IsNullOrEmpty() &&
                    builder.GroupBy.IsNullOrEmpty() &&
                    // 未指定查询字段的时候才默认加上排序，因为指定查询字段的很多时候是统计
                    (selects.IsNullOrWhiteSpace() || selects == "*")
                    )
                {
                    // 数字降序，其它升序
                    var b = fi.Type.IsInt();
                    builder.IsDesc = b;
                    // 修正没有设置builder.IsInt导致分页没有选择最佳的MaxMin的BUG，感谢 @RICH(20371423)
                    builder.IsInt = b;

                    builder.OrderBy = builder.KeyOrder;
                }
            }
            else
            {
                // 如果找不到唯一键，并且排序又为空，则采用全部字段一起，确保能够分页

                if (String.IsNullOrEmpty(builder.OrderBy)) builder.Key = Meta.FormatName(Meta.Table.PrimaryKeys.First().ColumnName);
            }
            return builder;
        }

        static SelectBuilder FixParam(SelectBuilder builder, IDictionary<String, Object> ps)
        {
            // 提取参数
            if (ps != null)
            {
                foreach (var item in ps)
                {
                    var dp = Meta.Session.Dal.Db.CreateParameter(item.Key, item.Value, Meta.Table.FindByName(item.Key)?.Type);

                    builder.Parameters.Add(dp);
                }
            }

            return builder;
        }
        #endregion

        #region 获取/设置 字段值
        /// <summary>获取/设置 字段值。</summary>
        /// <remarks>
        /// 一个索引，反射实现。
        /// 派生实体类可重写该索引，以避免发射带来的性能损耗。
        /// 基类已经实现了通用的快速访问，但是这里仍然重写，以增加控制，
        /// 比如字段名是属性名前面加上_，并且要求是实体字段才允许这样访问，否则一律按属性处理。
        /// </remarks>
        /// <param name="name">字段名</param>
        /// <returns></returns>
        public override Object this[String name]
        {
            get
            {
                // 匹配字段
                if (Meta.FieldNames.Contains(name))
                {
                    var field = GetType().GetFieldEx("_" + name, true);
                    if (field != null) return this.GetValue(field);
                }

                // 尝试匹配属性
                var property = GetType().GetPropertyEx(name, true);
                if (property != null && property.CanRead) return this.GetValue(property);

                // 检查动态增加的字段，返回默认值
                var f = Meta.Table.FindByName(name) as FieldItem;

                if (Extends.TryGetValue(name, out var obj))
                {
                    if (f != null && f.IsDynamic) return obj.ChangeType(f.Type);

                    return obj;
                }

                if (f != null && f.IsDynamic)
                {
                    return f.Type.CreateInstance();
                }

                return null;
            }
            set
            {
                //匹配字段
                if (Meta.FieldNames.Contains(name))
                {
                    var field = GetType().GetFieldEx("_" + name, true);
                    if (field != null)
                    {
                        this.SetValue(field, value);
                        return;
                    }
                }

                //尝试匹配属性
                var property = GetType().GetPropertyEx(name, true);
                if (property != null && property.CanWrite)
                {
                    this.SetValue(property, value);
                    return;
                }

                // 检查动态增加的字段，返回默认值
                if (Meta.Table.FindByName(name) is FieldItem f && f.IsDynamic) value = value.ChangeType(f.Type);

                Extends[name] = value;
            }
        }
        #endregion

        #region 导入导出XML/Json
        /// <summary>导入</summary>
        /// <param name="xml"></param>
        /// <returns></returns>
        [Obsolete("请使用 xml.ToXmlEntity<TEntity>()")]
        public static TEntity FromXml(String xml)
        {
            if (!String.IsNullOrEmpty(xml)) xml = xml.Trim();

            return xml.ToXmlEntity<TEntity>();
        }

        /// <summary>导入</summary>
        /// <param name="json"></param>
        /// <returns></returns>
        [Obsolete("请使用 json.ToJsonEntity<TEntity>()")]
        public static TEntity FromJson(String json)
        {
            return json.ToJsonEntity<TEntity>();
        }
        #endregion

        #region 克隆
        /// <summary>创建当前对象的克隆对象，仅拷贝基本字段</summary>
        /// <returns></returns>
        public override Object Clone() { return CloneEntity(); }

        /// <summary>克隆实体。创建当前对象的克隆对象，仅拷贝基本字段</summary>
        /// <param name="setDirty">是否设置脏数据。默认不设置</param>
        /// <returns></returns>
        public virtual TEntity CloneEntity(Boolean setDirty = false)
        {
            var obj = Meta.Factory.Create() as TEntity;
            foreach (var fi in Meta.Fields)
            {
                if (setDirty)
                    obj.SetItem(fi.Name, this[fi.Name]);
                else
                    obj[fi.Name] = this[fi.Name];
            }

            Extends.CopyTo(obj.Extends);

            return obj;
        }

        /// <summary>克隆实体</summary>
        /// <param name="setDirty"></param>
        /// <returns></returns>
        internal protected override IEntity CloneEntityInternal(Boolean setDirty = true) { return CloneEntity(setDirty); }
        #endregion

        #region 其它
        /// <summary>已重载。</summary>
        /// <returns></returns>
        public override String ToString()
        {
            // 优先主字段作为实体对象的字符串显示
            if (Meta.Master != null && Meta.Master != Meta.Unique) return this[Meta.Master.Name] + "";

            // 优先采用业务主键，也就是唯一索引
            var table = Meta.Table.DataTable;
            var dis = table.Indexes;
            if (dis != null && dis.Count > 0)
            {
                IDataIndex di = null;
                foreach (var item in dis)
                {
                    if (!item.Unique) continue;
                    if (item.Columns == null || item.Columns.Length < 1) continue;

                    var columns = table.GetColumns(item.Columns);
                    if (columns == null || columns.Length < 1) continue;

                    di = item;

                    // 如果不是唯一自增，再往下找别的。如果后面实在找不到，至少还有现在这个。
                    if (!(columns.Length == 1 && columns[0].Identity)) break;
                }

                if (di != null)
                {
                    var columns = table.GetColumns(di.Columns);

                    // [v1,v2,...vn]
                    var sb = new StringBuilder();
                    foreach (var dc in columns)
                    {
                        if (sb.Length > 0) sb.Append(",");
                        if (Meta.FieldNames.Contains(dc.Name)) sb.Append(this[dc.Name]);
                    }
                    if (columns.Length > 1)
                        return String.Format("[{0}]", sb.ToString());
                    else
                        return sb.ToString();
                }
            }

            var fs = Meta.FieldNames;
            if (fs.Contains("Name"))
                return this["Name"] + "";
            else if (fs.Contains("Title"))
                return this["Title"] + "";
            else if (fs.Contains("ID"))
                return this["ID"] + "";
            else
                return "实体" + typeof(TEntity).Name;
        }
        #endregion

        #region 脏数据
        /// <summary>设置所有数据的脏属性</summary>
        /// <param name="isDirty">改变脏属性的属性个数</param>
        /// <returns></returns>
        protected override Int32 SetDirty(Boolean isDirty)
        {
            var ds = Dirtys;
            if (ds == null || ds.Count < 1) return 0;

            var count = 0;
            foreach (var item in Meta.FieldNames)
            {
                var b = false;
                if (isDirty)
                {
                    if (!ds.TryGetValue(item, out b) || !b)
                    {
                        ds[item] = true;
                        count++;
                    }
                }
                else
                {
                    if (ds == null || ds.Count < 1) break;
                    if (ds.TryGetValue(item, out b) && b)
                    {
                        ds[item] = false;
                        count++;
                    }
                }
            }
            return count;
        }

        /// <summary>是否有脏数据。决定是否可以Update</summary>
        protected Boolean HasDirty
        {
            get
            {
                var ds = Dirtys;
                if (ds == null || ds.Count < 1) return false;

                foreach (var item in Meta.FieldNames)
                {
                    if (ds[item]) return true;
                }

                return false;
            }
        }

        ///// <summary>如果字段带有默认值，则需要设置脏数据，因为显然用户想设置该字段，而不是采用数据库的默认值</summary>
        ///// <param name="fieldName"></param>
        ///// <param name="newValue"></param>
        ///// <returns></returns>
        //protected override Boolean OnPropertyChanging(String fieldName, Object newValue)
        //{
        //    // 如果返回true，表示不相同，基类已经设置了脏数据
        //    if (base.OnPropertyChanging(fieldName, newValue)) return true;

        //    // 如果该字段存在，且带有默认值，则需要设置脏数据，因为显然用户想设置该字段，而不是采用数据库的默认值
        //    FieldItem fi = Meta.Table.FindByName(fieldName);
        //    if (fi != null && !String.IsNullOrEmpty(fi.DefaultValue))
        //    {
        //        Dirtys[fieldName] = true;
        //        return true;
        //    }

        //    return false;
        //}
        #endregion
    }
}