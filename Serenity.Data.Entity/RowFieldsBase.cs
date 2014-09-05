﻿using Serenity.ComponentModel;
using Serenity.Data.Mapping;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace Serenity.Data
{
    public partial class RowFieldsBase : Collection<Field>
    {
        internal Dictionary<string, Field> byName;
        internal Dictionary<string, Field> byPropertyName;
        internal bool isInitialized;
        internal string fieldPrefix;
        internal Dictionary<string, Join> joins;
        internal string localTextPrefix;
        internal PropertyChangedEventArgs[] propertyChangedEventArgs;
        internal PropertyDescriptorCollection propertyDescriptors;
        internal Func<Row> rowFactory;
        internal Type rowType;
        internal string connectionKey;
        internal string generationKey;
        internal object initializeLock;
        internal string tableName;

        protected RowFieldsBase(string tableName = null, string fieldPrefix = "")
        {
            this.tableName = tableName;
            this.fieldPrefix = fieldPrefix;
            this.byName = new Dictionary<string, Field>(StringComparer.OrdinalIgnoreCase);
            this.byPropertyName = new Dictionary<string, Field>(StringComparer.OrdinalIgnoreCase);
            this.joins = new Dictionary<string, Join>(StringComparer.OrdinalIgnoreCase);
            this.initializeLock = new object();

            DetermineRowType();
            DetermineTableName();
            DetermineConnectionKey();
            DetermineLocalTextPrefix();
        }

        private void DetermineRowType()
        {
            var fieldsType = this.GetType();
            if (!fieldsType.IsNested)
                throw new InvalidProgramException(String.Format(
                    "RowFields type {0} must be a nested type!", fieldsType.Name));

            this.rowType = fieldsType.DeclaringType;
            if (!this.rowType.IsSubclassOf(typeof(Row)))
                throw new InvalidProgramException(String.Format(
                    "RowFields {0}'s declaring row type {0} must be a subclass of Row!", fieldsType.Name, this.rowType.Name));

            var constructor = this.rowType.GetConstructor(Type.EmptyTypes);
            if (constructor != null)
            {
                var method = new DynamicMethod("", typeof(Row), Type.EmptyTypes);
                var il = method.GetILGenerator();
                il.Emit(OpCodes.Newobj, constructor);
                il.Emit(OpCodes.Ret);
                this.rowFactory = (Func<Row>)method.CreateDelegate(typeof(Func<>).MakeGenericType(typeof(Row)));
            }
        }

        private void DetermineTableName()
        {
            var attr = this.rowType.GetCustomAttribute<TableNameAttribute>();

            if (tableName != null)
            {
                if (attr != null && String.Compare(tableName, attr.Name, StringComparison.OrdinalIgnoreCase) != 0)
                    throw new InvalidProgramException(String.Format(
                        "Tablename in row type {0} can't be overridden by attribute!",
                            rowType.Name));

                return;
            }

            if (attr != null)
            {
                tableName = attr.Name;
                return;
            }

            var name = rowType.Name;
            if (name.EndsWith("Row"))
                name = name.Substring(0, name.Length - 3);

            tableName = name;
        }

        private void DetermineConnectionKey()
        {
            var connectionKeyAttr = rowType.GetCustomAttribute<ConnectionKeyAttribute>();
            if (connectionKeyAttr != null)
                this.connectionKey = connectionKeyAttr.Value;
            else
                this.connectionKey = "Default";
        }

        private void DetermineLocalTextPrefix()
        {
            if (localTextPrefix != null)
                return;

            if (connectionKey != null)
            {
                localTextPrefix = connectionKey + "." + tableName;
                return;
            }

            localTextPrefix = tableName;
            return;
        }

        private void GetRowFieldsAndProperties(
            out Dictionary<string, FieldInfo> rowFields,
            out Dictionary<string, PropertyInfo> rowProperties)
        {
            rowFields = new Dictionary<string, FieldInfo>(StringComparer.OrdinalIgnoreCase);
            rowProperties = new Dictionary<string, PropertyInfo>(StringComparer.OrdinalIgnoreCase);

            var members = rowType.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            foreach (var member in members)
            {
                var fi = member as FieldInfo;
                if (fi != null)
                    rowFields[fi.Name] = fi;
                else
                {
                    var pi = member as PropertyInfo;
                    if (pi != null)
                        rowProperties[pi.Name] = pi;
                }
            }
        }

        public void Initialize()
        {
            if (isInitialized)
                return;

            lock (this.initializeLock)
            {
                Dictionary<string, FieldInfo> rowFields;
                Dictionary<string, PropertyInfo> rowProperties;
                GetRowFieldsAndProperties(out rowFields, out rowProperties);

                foreach (var fieldInfo in this.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public))
                {
                    if (fieldInfo.FieldType.IsSubclassOf(typeof(Field)))
                    {
                        var field = (Field)fieldInfo.GetValue(this);

                        PropertyInfo property;
                        if (!rowProperties.TryGetValue(fieldInfo.Name, out property))
                            property = null;

                        ColumnAttribute column = null;
                        DisplayNameAttribute display = null;
                        SizeAttribute size = null;
                        ExpressionAttribute expression = null;
                        ScaleAttribute scale = null;
                        MinSelectLevelAttribute selectLevel = null;
                        ForeignKeyAttribute foreignKey = null;
                        LeftJoinAttribute foreignJoin = null;
                        DefaultValueAttribute defaultValue = null;

                        FieldFlags addFlags = (FieldFlags)0;
                        FieldFlags removeFlags = (FieldFlags)0;

                        if (property != null)
                        {
                            column = property.GetCustomAttribute<ColumnAttribute>(false);
                            display = property.GetCustomAttribute<DisplayNameAttribute>(false);
                            size = property.GetCustomAttribute<SizeAttribute>(false);
                            expression = property.GetCustomAttribute<ExpressionAttribute>(false);
                            scale = property.GetCustomAttribute<ScaleAttribute>(false);
                            selectLevel = property.GetCustomAttribute<MinSelectLevelAttribute>(false);
                            foreignKey = property.GetCustomAttribute<ForeignKeyAttribute>(false);
                            foreignJoin = property.GetCustomAttributes<LeftJoinAttribute>(false).FirstOrDefault(x => x.ToTable == null && x.OnCriteria == null);
                            defaultValue = property.GetCustomAttribute<DefaultValueAttribute>(false);

                            var insertable = property.GetCustomAttribute<InsertableAttribute>(false);
                            var updatable = property.GetCustomAttribute<UpdatableAttribute>(false);

                            if (insertable != null && !insertable.Value)
                                removeFlags |= FieldFlags.Insertable;

                            if (updatable != null && !updatable.Value)
                                removeFlags |= FieldFlags.Updatable;

                            foreach (var attr in property.GetCustomAttributes<SetFieldFlagsAttribute>(false))
                            {
                                addFlags |= attr.Add;
                                removeFlags |= attr.Remove;
                            }
                        }

                        if (field == null)
                        {
                            if (property == null)
                            {
                                throw new InvalidProgramException(String.Format(
                                    "Field {0} in type {1} is null and has no corresponding property in entity!",
                                        fieldInfo.Name, rowType.Name));
                            }

                            object[] prm = new object[7];
                            prm[0] = this; // owner
                            prm[1] = column == null ? property.Name : (column.Name.TrimToNull() ?? property.Name);
                            prm[2] = display != null ? new LocalText(display.DisplayName) : null;
                            prm[3] = size != null ? size.Value : 0;
                            prm[4] = (FieldFlags.Default ^ removeFlags) | addFlags;
                            prm[5] = null;
                            prm[6] = null;

                            FieldInfo storage;
                            if (rowFields.TryGetValue("_" + property.Name, out storage) ||
                                rowFields.TryGetValue("m_" + property.Name, out storage) ||
                                rowFields.TryGetValue(property.Name, out storage))
                            {
                                prm[5] = CreateFieldGetMethod(storage);
                                prm[6] = CreateFieldSetMethod(storage);
                            }

                            field = (Field)Activator.CreateInstance(fieldInfo.FieldType, prm);
                            fieldInfo.SetValue(this, field);
                        }
                        else
                        {
                            if (size != null)
                                throw new InvalidProgramException(String.Format(
                                    "Field size '{0}' in type {1} can't be overridden by Size attribute!",
                                        fieldInfo.Name, rowType.Name));

                            if (display != null)
                                field.Caption = new LocalText(display.DisplayName);

                            if ((int)addFlags != 0 || (int)removeFlags != 0)
                                field.Flags = (field.Flags ^ removeFlags) | addFlags;

                            if (column != null && String.Compare(column.Name, field.Name, StringComparison.OrdinalIgnoreCase) != 0)
                                throw new InvalidProgramException(String.Format(
                                    "Field name '{0}' in type {1} can't be overridden by Column name attribute!",
                                        fieldInfo.Name, rowType.Name));
                        }

                        if (scale != null)
                            field.Scale = scale.Value;

                        if (defaultValue != null)
                            field.DefaultValue = defaultValue.Value;

                        if (selectLevel != null)
                            field.MinSelectLevel = selectLevel.Value;

                        if (expression != null)
                            field.Expression = expression.Value;

                        if (foreignKey != null)
                        {
                            field.ForeignTable = foreignKey.Table;
                            field.ForeignField = foreignKey.Field;
                        }

                        if (foreignJoin != null)
                        {
                            field.ForeignJoinAlias = new LeftJoin(this.joins, field.ForeignTable, foreignJoin.Alias,
                                new Criteria(foreignJoin.Alias, field.ForeignField) == new Criteria(field));
                        }

                        if (property != null)
                        {
                            if (property.PropertyType != null &&
                                property.PropertyType.IsEnum &&
                                field is IEnumTypeField)
                            {
                                (field as IEnumTypeField).EnumType = property.PropertyType;
                            }
                            
                            foreach (var attr in property.GetCustomAttributes<LeftJoinAttribute>())
                                if (attr.ToTable != null && attr.OnCriteria != null)
                                    new LeftJoin(this.joins, attr.ToTable, attr.Alias,
                                        new Criteria(attr.Alias, attr.OnCriteria) == new Criteria(field));

                            field.PropertyName = property.Name;
                            this.byPropertyName[field.PropertyName] = field;

                            field.CustomAttributes = property.GetCustomAttributes(false);
                        }
                    }
                }

                foreach (var attr in this.rowType.GetCustomAttributes<LeftJoinAttribute>())
                    new LeftJoin(this.joins, attr.ToTable, attr.Alias, new Criteria(attr.OnCriteria));

                foreach (var attr in this.rowType.GetCustomAttributes<OuterApplyAttribute>())
                    new OuterApply(this.joins, attr.InnerQuery, attr.Alias);

                var propertyDescriptorArray = new PropertyDescriptor[this.Count];
                for (int i = 0; i < this.Count; i++)
                {
                    var field = this[i];
                    propertyDescriptorArray[i] = new FieldDescriptor(field);
                }

                this.propertyDescriptors = new PropertyDescriptorCollection(propertyDescriptorArray);

                InferTextualFields();
            }

            isInitialized = true;
        }

        private static Delegate CreateFieldGetMethod(FieldInfo fieldInfo)
        {
            Type[] arguments = new Type[1];
            arguments[0] = typeof(Row);

            var getter = new DynamicMethod(String.Concat("_Get", fieldInfo.Name, "_"),
                fieldInfo.FieldType, arguments, fieldInfo.DeclaringType);

            ILGenerator generator = getter.GetILGenerator();
            generator.Emit(OpCodes.Ldarg_0);
            generator.Emit(OpCodes.Castclass, fieldInfo.DeclaringType);
            generator.Emit(OpCodes.Ldfld, fieldInfo);
            generator.Emit(OpCodes.Ret);
            return getter.CreateDelegate(typeof(Func<,>).MakeGenericType(typeof(Row), fieldInfo.FieldType));
        }

        private static Delegate CreateFieldSetMethod(FieldInfo fieldInfo)
        {
            Type[] arguments = new Type[2];
            arguments[0] = typeof(Row);
            arguments[1] = fieldInfo.FieldType;

            var getter = new DynamicMethod(String.Concat("_Set", fieldInfo.Name, "_"),
                null, arguments, fieldInfo.DeclaringType);

            ILGenerator generator = getter.GetILGenerator();
            generator.Emit(OpCodes.Ldarg_0);
            generator.Emit(OpCodes.Castclass, fieldInfo.DeclaringType);
            generator.Emit(OpCodes.Ldarg_1);
            generator.Emit(OpCodes.Stfld, fieldInfo);
            generator.Emit(OpCodes.Ret);
            return getter.CreateDelegate(typeof(Action<,>).MakeGenericType(typeof(Row), fieldInfo.FieldType));
        }

        private void InferTextualFields()
        {
            foreach (var field in this)
            {
                if (!field.ForeignTable.IsNullOrEmpty() &&
                    field.TextualField == null)
                {
                    foreach (var join in this.joins.Values)
                    {
                        if (String.Compare(field.ForeignTable, join.Table) == 0 &&
                            join is LeftJoin &&
                            !Object.ReferenceEquals(null, join.OnCriteria) &&
                            join.OnCriteria.ToStringIgnoreParams().IndexOf(field.Expression, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            foreach (var f in this)
                                if (String.Compare(f.JoinAlias, join.Name, StringComparison.OrdinalIgnoreCase) == 0 &&
                                    f is StringField)
                                {
                                    field.TextualField = f.Name;
                                    break;
                                }
                        }

                        if (field.TextualField != null)
                            break;
                    }
                }
            }
        }

        public string TableName
        {
            get { return tableName; }
        }

        public string FieldPrefix
        {
            get { return fieldPrefix ?? ""; }
            set { fieldPrefix = value; }
        }

        public string LocalTextPrefix
        {
            get { return localTextPrefix ?? TableName; }
            set { localTextPrefix = value; }
        }

        public string Schema
        {
            get { return connectionKey; }
        }

        public string GenerationKey
        {
            get 
            {
                if (generationKey != null)
                    return generationKey;

                generationKey = (connectionKey + "." + TableName);
                return generationKey;
            }
            set
            {
                generationKey = value;
            }
        }

        protected override void InsertItem(int index, Field item)
        {
            if (isInitialized)
                throw new InvalidOperationException("field collection can't be modified!");

            if (item == null)
                throw new ArgumentNullException("item");

            if (byName.ContainsKey(item.Name))
                throw new ArgumentOutOfRangeException("item",
                    String.Format("field list already contains a field with name '{0}'", item.Name));

            if (item.Fields != null)
                item.Fields.Remove(item);

            base.InsertItem(index, item);

            item.Fields = this;
            item.Index = index;

            byName[item.Name] = item;
        }

        protected override void RemoveItem(int index)
        {
            if (isInitialized)
                throw new InvalidOperationException("field collection can't be modified!");

            var item = base[index];
            base.RemoveItem(index);
            item.Index = -1;
            item.Fields = null;
            byName.Remove(item.Name);
            for (int i = index; i < Count; i++)
                this[i].index = i;
        }

        protected override void SetItem(int index, Field item)
        {
            if (isInitialized)
                throw new InvalidOperationException("field collection can't be modified!");

            if (item == null)
                throw new ArgumentNullException("item");

            if (byName.ContainsKey(item.Name))
                throw new ArgumentOutOfRangeException("item", 
                    String.Format("field list already contains a field with name '{0}'", item.Name));

            var old = base[index];

            base.SetItem(index, item);

            old.Index = -1;
            old.Fields = null;
            byName.Remove(old.Name);

            item.Index = index;
            item.Fields = this;
            byName[item.Name] = item;
        }

        public Field FindField(string fieldName)
        {
            Field field;
            if (byName.TryGetValue(fieldName, out field))
                return field;
            else
                return null;
        }

        public Field FindFieldByPropertyName(string propertyName)
        {
            Field field;
            if (byPropertyName.TryGetValue(propertyName, out field))
                return field;
            else
                return null;
        }

        public IDictionary<string, Join> Joins
        {
            get { return joins; }
        }

        public IFilterFields FilterFields { get; set; }
    }
}