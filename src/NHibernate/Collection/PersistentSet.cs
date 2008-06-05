using System;
using System.Collections;
using System.Data;
using System.Diagnostics;
using Iesi.Collections;
using NHibernate.DebugHelpers;
using NHibernate.Engine;
using NHibernate.Loader;
using NHibernate.Persister.Collection;
using NHibernate.Type;
using NHibernate.Util;

namespace NHibernate.Collection
{
	/// <summary>
	/// .NET has no design equivalent for Java's Set so we are going to use the
	/// Iesi.Collections library. This class is internal to NHibernate and shouldn't
	/// be used by user code.
	/// </summary>
	/// <remarks>
	/// The code for the Iesi.Collections library was taken from the article
	/// <a href="http://www.codeproject.com/csharp/sets.asp">Add Support for "Set" Collections
	/// to .NET</a> that was written by JasonSmith.
	/// </remarks>
	[Serializable]
	[DebuggerTypeProxy(typeof (CollectionProxy))]
	public class PersistentSet : AbstractPersistentCollection, ISet
	{
		/// <summary>
		/// The <see cref="ISet"/> that NHibernate is wrapping.
		/// </summary>
		protected ISet _set;

		/// <summary>
		/// A temporary list that holds the objects while the PersistentSet is being
		/// populated from the database.  
		/// </summary>
		/// <remarks>
		/// This is necessary to ensure that the object being added to the PersistentSet doesn't
		/// have its' <c>GetHashCode()</c> and <c>Equals()</c> methods called during the load
		/// process.
		/// </remarks>
		[NonSerialized] private IList tempList;

		public PersistentSet() {} // needed for serialization

		/// <summary> 
		/// Constructor matching super.
		/// Instantiates a lazy set (the underlying set is un-initialized).
		/// </summary>
		/// <param name="session">The session to which this set will belong. </param>
		public PersistentSet(ISessionImplementor session) : base(session) {}

		/// <summary> 
		/// Instantiates a non-lazy set (the underlying set is constructed
		/// from the incoming set reference).
		/// </summary>
		/// <param name="session">The session to which this set will belong. </param>
		/// <param name="original">The underlying set data. </param>
		public PersistentSet(ISessionImplementor session, ISet original) : base(session)
		{
			// Sets can be just a view of a part of another collection.
			// do we need to copy it to be sure it won't be changing
			// underneath us?
			// ie. this.set.addAll(set);
			_set = original;
			SetInitialized();
			IsDirectlyAccessible = true;
		}

		public override bool RowUpdatePossible
		{
			get { return false; }
		}

		public override ICollection GetSnapshot(ICollectionPersister persister)
		{
			EntityMode entityMode = Session.EntityMode;

			//if (set==null) return new Set(session);
			Hashtable clonedSet = new Hashtable(_set.Count);
			foreach (object current in _set)
			{
				object copied = persister.ElementType.DeepCopy(current, entityMode, persister.Factory);
				clonedSet[copied] = copied;
			}
			return clonedSet;
		}

		public override ICollection GetOrphans(object snapshot, string entityName)
		{
			IDictionary sn = (IDictionary) snapshot;
			// NH Different implementation : sn.Keys return a new collection we don't need "re-new"
			return GetOrphans(sn.Keys, _set, entityName, Session);
		}

		public override bool EqualsSnapshot(ICollectionPersister persister)
		{
			IType elementType = persister.ElementType;
			IDictionary snapshot = (IDictionary) GetSnapshot();
			if (snapshot.Count != _set.Count)
			{
				return false;
			}
			else
			{
				foreach (object obj in _set)
				{
					object oldValue = snapshot[obj];
					if (oldValue == null || elementType.IsDirty(oldValue, obj, Session))
					{
						return false;
					}
				}
			}

			return true;
		}

		public override bool IsSnapshotEmpty(object snapshot)
		{
			return ((IDictionary) snapshot).Count == 0;
		}

		public override void BeforeInitialize(ICollectionPersister persister, int anticipatedSize)
		{
			_set = (ISet) persister.CollectionType.Instantiate(anticipatedSize);
		}

		/// <summary>
		/// Initializes this PersistentSet from the cached values.
		/// </summary>
		/// <param name="persister">The CollectionPersister to use to reassemble the PersistentSet.</param>
		/// <param name="disassembled">The disassembled PersistentSet.</param>
		/// <param name="owner">The owner object.</param>
		public override void InitializeFromCache(ICollectionPersister persister, object disassembled, object owner)
		{
			object[] array = (object[]) disassembled;
			int size = array.Length;
			BeforeInitialize(persister, size);
			for (int i = 0; i < size; i++)
			{
				object element = persister.ElementType.Assemble(array[i], Session, owner);
				if (element != null)
				{
					_set.Add(element);
				}
			}
			SetInitialized();
		}

		public override bool Empty
		{
			get { return _set.Count == 0; }
		}

		public override string ToString()
		{
			Read();
			return StringHelper.CollectionToString(_set);
		}

		public override object ReadFrom(IDataReader rs, ICollectionPersister role, ICollectionAliases descriptor, object owner)
		{
			object element = role.ReadElement(rs, owner, descriptor.SuffixedElementAliases, Session);
			if (element != null)
			{
				tempList.Add(element);
			}
			return element;
		}

		/// <summary>
		/// Set up the temporary List that will be used in the EndRead() 
		/// to fully create the set.
		/// </summary>
		public override void BeginRead()
		{
			base.BeginRead();
			tempList = new ArrayList();
		}

		/// <summary>
		/// Takes the contents stored in the temporary list created during <c>BeginRead()</c>
		/// that was populated during <c>ReadFrom()</c> and write it to the underlying 
		/// PersistentSet.
		/// </summary>
		public override bool EndRead(ICollectionPersister persister)
		{
			_set.AddAll(tempList);
			tempList = null;
			SetInitialized();
			return true;
		}

		public override IEnumerable Entries(ICollectionPersister persister)
		{
			return _set;
		}

		public override object Disassemble(ICollectionPersister persister)
		{
			object[] result = new object[_set.Count];
			int i = 0;

			foreach (object obj in _set)
			{
				result[i++] = persister.ElementType.Disassemble(obj, Session, null);
			}
			return result;
		}

		public override IEnumerable GetDeletes(ICollectionPersister persister, bool indexIsFormula)
		{
			IType elementType = persister.ElementType;
			IDictionary sn = (IDictionary) GetSnapshot();
			ArrayList deletes = new ArrayList(sn.Count);
			foreach (object obj in sn.Keys)
			{
				if (!_set.Contains(obj))
				{
					// the element has been removed from the set
					deletes.Add(obj);
				}
			}
			foreach (object obj in _set)
			{
				object oldValue = sn[obj];
				if (oldValue != null && elementType.IsDirty(obj, oldValue, Session))
				{
					// the element has changed
					deletes.Add(oldValue);
				}
			}

			return deletes;
		}

		public override bool NeedsInserting(object entry, int i, IType elemType)
		{
			IDictionary sn = (IDictionary) GetSnapshot();
			object oldKey = sn[entry];
			// note that it might be better to iterate the snapshot but this is safe,
			// assuming the user implements equals() properly, as required by the PersistentSet
			// contract!
			return oldKey == null || elemType.IsDirty(oldKey, entry, Session);
		}

		public override bool NeedsUpdating(object entry, int i, IType elemType)
		{
			return false;
		}

		public override object GetIndex(object entry, int i, ICollectionPersister persister)
		{
			throw new NotSupportedException("Sets don't have indexes");
		}

		public override object GetElement(object entry)
		{
			return entry;
		}

		public override object GetSnapshotElement(object entry, int i)
		{
			throw new NotSupportedException("Sets don't support updating by element");
		}

		public override bool Equals(object other)
		{
			ICollection that = other as ICollection;
			if (that == null)
			{
				return false;
			}
			Read();
			return CollectionHelper.CollectionEquals(_set, that);
		}

		public override int GetHashCode()
		{
			Read();
			return _set.GetHashCode();
		}

		public override bool EntryExists(object entry, int i)
		{
			return true;
		}

		public override bool IsWrapper(object collection)
		{
			return _set == collection;
		}

		#region ISet Members

		public ISet Union(ISet a)
		{
			Read();
			return _set.Union(a);
		}

		public ISet Intersect(ISet a)
		{
			Read();
			return _set.Intersect(a);
		}

		public ISet Minus(ISet a)
		{
			Read();
			return _set.Minus(a);
		}

		public ISet ExclusiveOr(ISet a)
		{
			Read();
			return _set.ExclusiveOr(a);
		}

		public bool Contains(object o)
		{
			bool? exists = ReadElementExistence(o);
			return exists == null ? _set.Contains(o) : exists.Value;
		}

		public bool ContainsAll(ICollection c)
		{
			Read();
			return _set.ContainsAll(c);
		}

		public bool Add(object o)
		{
			bool? exists = IsOperationQueueEnabled ? ReadElementExistence(o) : null;
			if (!exists.HasValue)
			{
				Initialize(true);
				if (_set.Add(o))
				{
					Dirty();
					return true;
				}
				else
				{
					return false;
				}
			}
			else if (exists.Value)
			{
				return false;
			}
			else
			{
				QueueOperation(new SimpleAddDelayedOperation(this, o));
				return true;
			}
		}

		public bool AddAll(ICollection c)
		{
			if (c.Count > 0)
			{
				Initialize(true);
				if (_set.AddAll(c))
				{
					Dirty();
					return true;
				}
				else
				{
					return false;
				}
			}
			else
			{
				return false;
			}
		}

		public bool Remove(object o)
		{
			bool? exists = PutQueueEnabled ? ReadElementExistence(o) : null;
			if (!exists.HasValue)
			{
				Initialize(true);
				bool contained = _set.Remove(o);
				if (contained)
				{
					Dirty();
					return true;
				}
				else
				{
					return false;
				}
			}
			else if (exists.Value)
			{
				QueueOperation(new SimpleRemoveDelayedOperation(this, o));
				return true;
			}
			else
			{
				return false;
			}
		}

		public bool RemoveAll(ICollection c)
		{
			if (c.Count > 0)
			{
				Initialize(true);
				if (_set.RemoveAll(c))
				{
					Dirty();
					return true;
				}
				else
				{
					return false;
				}
			}
			else
			{
				return false;
			}
		}

		public bool RetainAll(ICollection c)
		{
			Initialize(true);
			if (_set.RetainAll(c))
			{
				Dirty();
				return true;
			}
			else
			{
				return false;
			}
		}

		public void Clear()
		{
			if (ClearQueueEnabled)
			{
				QueueOperation(new ClearDelayedOperation(this));
			}
			else
			{
				Initialize(true);
				if (!(_set.Count == 0))
				{
					_set.Clear();
					Dirty();
				}
			}
		}

		public bool IsEmpty
		{
			get { return ReadSize() ? CachedSize == 0 : (_set.Count == 0); }
		}

		#endregion

		#region ICollection Members

		public void CopyTo(Array array, int index)
		{
			// NH : we really need to initialize the set ?
			Read();
			_set.CopyTo(array, index);
		}

		public int Count
		{
			get { return ReadSize() ? CachedSize : _set.Count; }
		}

		public object SyncRoot
		{
			get { return this; }
		}

		public bool IsSynchronized
		{
			get { return false; }
		}

		#endregion

		#region IEnumerable Members

		public IEnumerator GetEnumerator()
		{
			Read();
			return _set.GetEnumerator();
		}

		#endregion

		#region ICloneable Members

		public object Clone()
		{
			Read();
			return _set.Clone();
		}

		#endregion

		#region DelayedOperations

		protected sealed class ClearDelayedOperation : IDelayedOperation
		{
			private readonly PersistentSet enclosingInstance;

			public ClearDelayedOperation(PersistentSet enclosingInstance)
			{
				this.enclosingInstance = enclosingInstance;
			}

			public object AddedInstance
			{
				get { return null; }
			}

			public object Orphan
			{
				get { throw new NotSupportedException("queued clear cannot be used with orphan delete"); }
			}

			public void Operate()
			{
				enclosingInstance._set.Clear();
			}
		}

		protected sealed class SimpleAddDelayedOperation : IDelayedOperation
		{
			private readonly PersistentSet enclosingInstance;
			private readonly object value;

			public SimpleAddDelayedOperation(PersistentSet enclosingInstance, object value)
			{
				this.enclosingInstance = enclosingInstance;
				this.value = value;
			}

			public object AddedInstance
			{
				get { return value; }
			}

			public object Orphan
			{
				get { return null; }
			}

			public void Operate()
			{
				enclosingInstance._set.Add(value);
			}
		}

		protected sealed class SimpleRemoveDelayedOperation : IDelayedOperation
		{
			private readonly PersistentSet enclosingInstance;
			private readonly object value;

			public SimpleRemoveDelayedOperation(PersistentSet enclosingInstance, object value)
			{
				this.enclosingInstance = enclosingInstance;
				this.value = value;
			}

			public object AddedInstance
			{
				get { return null; }
			}

			public object Orphan
			{
				get { return value; }
			}

			public void Operate()
			{
				enclosingInstance._set.Remove(value);
			}
		}

		#endregion
	}
}