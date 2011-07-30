﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BEPUphysics.Entities.Prefabs;
using BEPUphysics.UpdateableSystems;
using BEPUphysics;
using Microsoft.Xna.Framework;
using BEPUphysics.MathExtensions;
using BEPUphysics.Collidables.MobileCollidables;
using BEPUphysics.BroadPhaseSystems;
using BEPUphysics.NarrowPhaseSystems.Pairs;
using BEPUphysics.Materials;
using BEPUphysics.PositionUpdating;
using BEPUphysics.DataStructures;
using System.Diagnostics;
using BEPUphysics.CollisionShapes.ConvexShapes;
using BEPUphysics.Collidables;
using Microsoft.Xna.Framework.Input;
using BEPUphysics.Entities;

namespace BEPUphysicsDemos.AlternateMovement.Character
{
    public class CharacterController : Updateable, IBeforeSolverUpdateable, IBeforePositionUpdateUpdateable, IEndOfTimeStepUpdateable
    {
        public Cylinder Body { get; private set; }

        public Stepper Stepper { get; private set; }


        public HorizontalMotionConstraint HorizontalMotionConstraint { get; private set; }

        public float JumpSpeed = 4.5f;
        public float SlidingJumpSpeed = 3;
        float jumpForceFactor = 1f;
        /// <summary>
        /// Gets or sets the amount of force to apply to supporting dynamic entities as a fraction of the force used to reach the jump speed.
        /// </summary>
        public float JumpForceFactor
        {
            get
            {
                return jumpForceFactor;
            }
            set
            {
                if (value < 0)
                    throw new Exception("Value must be nonnegative.");
                jumpForceFactor = value;
            }
        }

        /// <summary>
        /// Gets the support finder used by the character.
        /// The support finder analyzes the character's contacts to see if any of them provide support and/or traction.
        /// </summary>
        public SupportFinder SupportFinder { get; private set; }


        /// <summary>
        /// Gets or sets the maximum change in speed that the character will apply in order to stay connected to the ground.
        /// </summary>
        public float GlueSpeed { get; set; }

        ///// <summary>
        ///// Gets or sets the multiplier of horizontal force to apply to support objects when standing on top of dynamic entities.
        ///// </summary>
        //public float HorizontalForceFactor { get; set; }


        SupportData supportData;

        public CharacterController()
        {
            Body = new Cylinder(Vector3.Zero, 1.7f, .6f, 10);
            Body.CollisionInformation.Shape.CollisionMargin = .1f;
            //Making the character a continuous object prevents it from flying through walls which would be pretty jarring from a player's perspective.
            Body.PositionUpdateMode = PositionUpdateMode.Continuous;
            Body.LocalInertiaTensorInverse = new Matrix3X3();
            Body.CollisionInformation.Events.CreatingPair += RemoveFriction;
            Body.LinearDamping = 0;
            GlueSpeed = 20;
            SupportFinder = new SupportFinder(this);
            HorizontalMotionConstraint = new HorizontalMotionConstraint(this);
            Stepper = new Stepper(this);


            Entity = Body;
        }

        void RemoveFriction(EntityCollidable sender, BroadPhaseEntry other, INarrowPhasePair pair)
        {
            var collidablePair = pair as CollidablePairHandler;
            if (collidablePair != null)
            {
                //The default values for InteractionProperties is all zeroes- zero friction, zero bounciness.
                //That's exactly how we want the character to behave when hitting objects.
                collidablePair.UpdateMaterialProperties(new InteractionProperties());
            }
        }

        void ExpandBoundingBox()
        {
            if (Body.IsActive)
            {
                //This runs after the bounding box updater is run, but before the broad phase.
                //Expanding the character's bounding box ensures that minor variations in velocity will not cause
                //any missed information.
                //For a character which is not bound to Vector3.Up (such as a character that needs to run around a spherical planet),
                //the bounding box expansion needs to be changed such that it includes the full motion of the character.
                float radius = Body.CollisionInformation.Shape.CollisionMargin * 1.1f; //The character can teleport by its collision margin when stepping up.
#if WINDOWS
                Vector3 offset;
#else
            Vector3 offset = new Vector3();
#endif
                offset.X = radius;
                offset.Y = Stepper.MaximumStepHeight;
                offset.Z = radius;
                BoundingBox box = Body.CollisionInformation.BoundingBox;
                Vector3.Add(ref box.Max, ref offset, out box.Max);
                Vector3.Subtract(ref box.Min, ref offset, out box.Min);
                Body.CollisionInformation.BoundingBox = box;
            }


        }

        public static Entity Entity;

        void CollectSupportData()
        {           
            //Identify supports.
            SupportFinder.UpdateSupports();

            //Collect the support data from the support, if any.
            if (SupportFinder.HasSupport)
            {
                if (SupportFinder.HasTraction)
                    supportData = SupportFinder.TractionData.Value;
                else
                    supportData = SupportFinder.SupportData.Value;
            }
            else
                supportData = new SupportData();
        }

        void IBeforeSolverUpdateable.Update(float dt)
        {

            bool hadTraction = SupportFinder.HasTraction;

            CollectSupportData();


            //Compute the initial velocities relative to the support.
            Vector3 relativeVelocity;
            ComputeRelativeVelocity(out relativeVelocity);
            float verticalVelocity = Vector3.Dot(supportData.Normal, relativeVelocity);
            Vector3 horizontalVelocity = relativeVelocity - supportData.Normal * verticalVelocity;



            //Don't attempt to use an object as support if we are flying away from it (and we were never standing on it to begin with).
            if (SupportFinder.HasTraction && !hadTraction && verticalVelocity < 0)
            {
                SupportFinder.ClearSupportData();
                HorizontalMotionConstraint.SupportData = new SupportData();
            }

            //Attempt to jump.
            if (tryToJump)
            {
                //In the following, note that the jumping velocity changes are computed such that the separating velocity is specifically achieved,
                //rather than just adding some speed along an arbitrary direction.  This avoids some cases where the character could otherwise increase
                //the jump speed, which may not be desired.
                if (SupportFinder.HasTraction)
                {
                    //The character has traction, so jump straight up.
                    float currentUpVelocity = Vector3.Dot(Body.OrientationMatrix.Up, relativeVelocity);
                    //Target velocity is JumpSpeed.
                    float velocityChange = JumpSpeed - currentUpVelocity;
                    ApplyJumpVelocity(Body.OrientationMatrix.Up * velocityChange, ref relativeVelocity);
                }
                else if (SupportFinder.HasSupport)
                {
                    //The character does not have traction, so jump along the surface normal instead.
                    float currentNormalVelocity = Vector3.Dot(supportData.Normal, relativeVelocity);
                    //Target velocity is JumpSpeed.
                    float velocityChange = SlidingJumpSpeed - currentNormalVelocity;
                    ApplyJumpVelocity(supportData.Normal * -velocityChange, ref relativeVelocity);
                }
                SupportFinder.ClearSupportData();
                tryToJump = false;
                HorizontalMotionConstraint.SupportData = new SupportData();

            }


            //Try to step!
            if (Keyboard.GetState().IsKeyDown(Keys.O))
                Debug.WriteLine("Breka.");
            Vector3 newPosition;
            if (Stepper.TryToStepDown(out newPosition) ||
                Stepper.TryToStepUp(out newPosition))
            {
                Body.Position = newPosition;
                var orientation = Body.Orientation;
                //The re-do of contacts won't do anything unless we update the collidable's world transform.
                Body.CollisionInformation.UpdateWorldTransform(ref newPosition, ref orientation);
                //Refresh all the narrow phase collisions.
                foreach (var pair in Body.CollisionInformation.Pairs)
                {
                    pair.UpdateCollision(dt);
                }
                //Also re-collect supports.
                //This will ensure the constraint and other velocity affectors have the most recent information available.
                CollectSupportData();
                ComputeRelativeVelocity(out relativeVelocity);
                verticalVelocity = Vector3.Dot(supportData.Normal, relativeVelocity);
                horizontalVelocity = relativeVelocity - supportData.Normal * verticalVelocity;

            }

            //if (SupportFinder.HasTraction && SupportFinder.Supports.Count == 0)
            //{
            //There's another way to step down that is a lot cheaper, but less robust.
            //This modifies the velocity of the character to make it fall faster.
            //Impacts with the ground will be harder, so it will apply superfluous force to supports.
            //Additionally, it will not be consistent with instant up-stepping.
            //However, because it does not do any expensive queries, it is very fast!

            ////We are being supported by a ray cast, but we're floating.
            ////Let's try to get to the ground faster.
            ////How fast?  Try picking an arbitrary velocity and setting our relative vertical velocity to that value.
            ////Don't go farther than the maximum distance, though.
            //float maxVelocity = (SupportFinder.SupportRayData.Value.HitData.T - SupportFinder.RayLengthToBottom);
            //if (maxVelocity > 0)
            //{
            //    maxVelocity = (maxVelocity + .01f) / dt;

            //    float targetVerticalVelocity = -3;
            //    verticalVelocity = Vector3.Dot(Body.OrientationMatrix.Up, relativeVelocity);
            //    float change = MathHelper.Clamp(targetVerticalVelocity - verticalVelocity, -maxVelocity, 0);
            //    ChangeVelocityUnilaterally(Body.OrientationMatrix.Up * change, ref relativeVelocity);
            //}
            //}


            //Also manage the vertical velocity of the character;
            //don't let it separate from the ground.
            if (SupportFinder.HasTraction)
            {
                verticalVelocity += Math.Max(supportData.Depth / dt, 0);
                if (verticalVelocity < 0 && verticalVelocity > -GlueSpeed)
                {
                    ChangeVelocityUnilaterally(-supportData.Normal * verticalVelocity, ref relativeVelocity);
                }

            }


            //Warning:
            //Changing a constraint's support data is not thread safe; it modifies simulation islands!
            HorizontalMotionConstraint.SupportData = supportData;







        }

        void ComputeRelativeVelocity(out Vector3 relativeVelocity)
        {

            //Compute the relative velocity between the body and its support, if any.
            //The relative velocity will be updated as impulses are applied.
            relativeVelocity = Body.LinearVelocity;
            if (SupportFinder.HasSupport)
            {
                //Only entities has velocity.
                var entityCollidable = supportData.SupportObject as EntityCollidable;
                if (entityCollidable != null)
                {
                    Vector3 entityVelocity = Toolbox.GetVelocityOfPoint(supportData.Position, entityCollidable.Entity);
                    Vector3.Subtract(ref relativeVelocity, ref entityVelocity, out relativeVelocity);
                }
            }

        }

        /// <summary>
        /// Changes the relative velocity between the character and its support.
        /// </summary>
        /// <param name="velocityChange">Change to apply to the character and support relative velocity.</param>
        /// <param name="relativeVelocity">Relative velocity to update.</param>
        void ApplyJumpVelocity(Vector3 velocityChange, ref Vector3 relativeVelocity)
        {

            Body.LinearVelocity += velocityChange;
            var entityCollidable = supportData.SupportObject as EntityCollidable;
            if (entityCollidable != null)
            {
                if (entityCollidable.Entity.IsDynamic)
                {
                    Vector3 change = velocityChange * jumpForceFactor;
                    entityCollidable.Entity.LinearMomentum += change * -Body.Mass;
                    velocityChange += change;
                }
            }

            //Update the relative velocity as well.  It's a ref parameter, so this update will be reflected in the calling scope.
            Vector3.Add(ref relativeVelocity, ref velocityChange, out relativeVelocity);

        }

        /// <summary>
        /// In some cases, an applied velocity should only modify the character.
        /// This allows partially non-physical behaviors, like gluing the character to the ground.
        /// </summary>
        /// <param name="velocityChange">Change to apply to the character.</param>
        /// <param name="relativeVelocity">Relative velocity to update.</param>
        void ChangeVelocityUnilaterally(Vector3 velocityChange, ref Vector3 relativeVelocity)
        {
            Body.LinearVelocity += velocityChange;
            //Update the relative velocity as well.  It's a ref parameter, so this update will be reflected in the calling scope.
            Vector3.Add(ref relativeVelocity, ref velocityChange, out relativeVelocity);

        }




        void IBeforePositionUpdateUpdateable.Update(float dt)
        {
            //Also manage the vertical velocity of the character;
            //don't let it separate from the ground.
            if (SupportFinder.HasTraction)
            {
                Vector3 relativeVelocity;
                ComputeRelativeVelocity(out relativeVelocity);
                float verticalVelocity = Vector3.Dot(supportData.Normal, relativeVelocity);
                verticalVelocity += Math.Max(supportData.Depth / dt, 0);
                if (verticalVelocity < 0 && verticalVelocity > -GlueSpeed)
                {
                    ChangeVelocityUnilaterally(-supportData.Normal * verticalVelocity, ref relativeVelocity);
                }

            }
        }


        void IEndOfTimeStepUpdateable.Update(float dt)
        {
            //Teleport the object to the first hit surface.
            //This has to be done after the position update to ensure that no other systems get a chance to make an invalid state visible to the user, which would be corrected
            //jerkily in a subsequent frame.
            //Consider using forces instead.

            //if (IsSupported)
            //    Body.Position += -(goalSupportT - supportData.T) * sweep;
        }

        bool tryToJump = false;
        /// <summary>
        /// Jumps the character off of whatever it's currently standing on.  If it has traction, it will go straight up.
        /// If it doesn't have traction, but is still supported by something, it will jump in the direction of the surface normal.
        /// </summary>
        public void Jump()
        {
            //The actual jump velocities are applied next frame.  This ensures that gravity doesn't pre-emptively slow the jump, and uses more
            //up-to-date support data.
            tryToJump = true;
        }

        public override void OnAdditionToSpace(ISpace newSpace)
        {
            //Add any supplements to the space too.
            newSpace.Add(Body);
            newSpace.Add(HorizontalMotionConstraint);
            //This character controller requires the standard implementation of Space.
            ((Space)newSpace).BoundingBoxUpdater.Finishing += ExpandBoundingBox;

            Body.AngularVelocity = new Vector3();
            Body.LinearVelocity = new Vector3();
        }
        public override void OnRemovalFromSpace(ISpace oldSpace)
        {
            //Remove any supplements from the space too.
            oldSpace.Remove(Body);
            oldSpace.Remove(HorizontalMotionConstraint);
            //This character controller requires the standard implementation of Space.
            ((Space)oldSpace).BoundingBoxUpdater.Finishing -= ExpandBoundingBox;
            SupportFinder.ClearSupportData();
            Body.AngularVelocity = new Vector3();
            Body.LinearVelocity = new Vector3();
        }


    }
}
