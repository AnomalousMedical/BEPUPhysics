﻿using System;
using BEPUphysics.BroadPhaseSystems;
using BEPUphysics.Collidables;
using BEPUphysics.Collidables.MobileCollidables;
using BEPUphysics.CollisionTests;
using BEPUphysics.CollisionTests.CollisionAlgorithms.GJK;
using BEPUphysics.CollisionTests.Manifolds;
using BEPUphysics.Constraints.Collision;
using BEPUphysics.DataStructures;
using BEPUphysics.PositionUpdating;
using BEPUphysics.Settings;
using Microsoft.Xna.Framework;
using BEPUphysics.MathExtensions;
using BEPUphysics.ResourceManagement;
using BEPUphysics.CollisionShapes.ConvexShapes;

namespace BEPUphysics.NarrowPhaseSystems.Pairs
{
    ///<summary>
    /// Handles a standard pair handler that has a direct manifold and constraint.
    ///</summary>
    public abstract class Type1PairHandler : CollidablePairHandler
    {

        protected abstract ContactManifold ContactManifold { get; }
        protected abstract ContactManifoldConstraint ContactConstraint { get; }

        ///<summary>
        /// Constructs a pair handler.
        ///</summary>
        protected Type1PairHandler()
        {
            //Child type constructors construct manifold first.
            ContactManifold.ContactAdded += OnContactAdded;
            ContactManifold.ContactRemoved += OnContactRemoved;
        }

        protected override void OnContactAdded(Contact contact)
        {
            ContactConstraint.AddContact(contact);
            base.OnContactAdded(contact);

        }

        protected override void OnContactRemoved(Contact contact)
        {
            ContactConstraint.RemoveContact(contact);
            base.OnContactRemoved(contact);

        }

        ///<summary>
        /// Initializes the pair handler.
        ///</summary>
        ///<param name="entryA">First entry in the pair.</param>
        ///<param name="entryB">Second entry in the pair.</param>
        public override void Initialize(BroadPhaseEntry entryA, BroadPhaseEntry entryB)
        {
            //Child initialization is responsible for setting up the entries.

            ContactManifold.Initialize(CollidableA, CollidableB);
            ContactConstraint.Initialize(EntityA, EntityB, this);

            base.Initialize(entryA, entryB);

        }

        ///<summary>
        /// Forces an update of the pair's material properties.
        ///</summary>
        public override void UpdateMaterialProperties()
        {
            ContactConstraint.UpdateMaterialProperties(
              EntityA == null ? null : EntityA.material,
              EntityB.material == null ? null : EntityB.material);
        }



        ///<summary>
        /// Cleans up the pair handler.
        ///</summary>
        public override void CleanUp()
        {
            //Deal with the remaining contacts.
            for (int i = ContactManifold.contacts.count - 1; i >= 0; i--)
            {
                OnContactRemoved(ContactManifold.contacts[i]);
            }

            //If the constraint is still in the solver, then request to have it removed.
            if (ContactConstraint.solver != null)
            {
                ContactConstraint.pair = null; //Setting the pair to null tells the constraint that it's going to be orphaned.  It will be cleaned up on removal.
                if (Parent != null)
                    Parent.RemoveSolverUpdateable(ContactConstraint);
                else if (NarrowPhase != null)
                    NarrowPhase.EnqueueRemovedSolverUpdateable(ContactConstraint);
            }
            else
                ContactConstraint.CleanUpReferences();//The constraint isn't in the solver, so we can safely clean it up directly.

            ContactConstraint.CleanUp();


            base.CleanUp();

            ContactManifold.CleanUp();


            //Child cleanup is responsible for cleaning up direct references to the involved collidables.
        }



        ///<summary>
        /// Updates the pair handler.
        ///</summary>
        ///<param name="dt">Timestep duration.</param>
        public override void UpdateCollision(float dt)
        {

            if (!suppressEvents)
            {
                CollidableA.EventTriggerer.OnPairUpdated(CollidableB, this);
                CollidableB.EventTriggerer.OnPairUpdated(CollidableA, this);
            }

            ContactManifold.Update(dt);

            if (ContactManifold.contacts.count > 0)
            {
                if (!suppressEvents)
                {
                    CollidableA.EventTriggerer.OnPairTouching(CollidableB, this);
                    CollidableB.EventTriggerer.OnPairTouching(CollidableA, this);
                }

                if (previousContactCount == 0)
                {
                    //New collision.

                    //Add a solver item.
                    if (Parent != null)
                        Parent.AddSolverUpdateable(ContactConstraint);
                    else if (NarrowPhase != null)
                        NarrowPhase.EnqueueGeneratedSolverUpdateable(ContactConstraint);

                    //And notify the pair members.
                    if (!suppressEvents)
                    {
                        CollidableA.EventTriggerer.OnInitialCollisionDetected(CollidableB, this);
                        CollidableB.EventTriggerer.OnInitialCollisionDetected(CollidableA, this);
                    }
                }
            }
            else if (previousContactCount > 0)
            {
                //Just exited collision.

                //Remove the solver item.
                if (Parent != null)
                    Parent.RemoveSolverUpdateable(ContactConstraint);
                else if (NarrowPhase != null)
                    NarrowPhase.EnqueueRemovedSolverUpdateable(ContactConstraint);

                if (!suppressEvents)
                {
                    CollidableA.EventTriggerer.OnCollisionEnded(CollidableB, this);
                    CollidableB.EventTriggerer.OnCollisionEnded(CollidableA, this);
                }
            }
            previousContactCount = ContactManifold.contacts.count;

        }



        public override int ContactCount
        {
            get { return ContactManifold.contacts.count; }
        }

    }

}