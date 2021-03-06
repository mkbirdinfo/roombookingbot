﻿using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Linq;
using System.Xml;
using JamesMann.BotFramework.Middleware;
using Microsoft.Bot.Builder.Dialogs;
using Microsoft.Bot.Schema;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RoomBookingBot.Chatbot.Bots.DialogStateWrappers;
using RoomBookingBot.Chatbot.Extensions;
using RoomBookingBot.Chatbot.Model;

namespace RoomBookingBot.Chatbot.Bots.Dialogs.Intents.CheckRoomAvailability.SearchAndConfirm
{
    public class SearchGraphDialog : DialogContainer
    {
        public static SearchGraphDialog Instance = new SearchGraphDialog();

        private SearchGraphDialog() : base(Id)
        {
            Dialogs.Add(Id, new WaterfallStep[]
            {
                async (dc, args, next) =>
                {
                    var stateWrapper = new SearchGraphDialogStateWrapper(dc.ActiveDialog.State) {Booking = (BookingRequest) args["bookingRequest"]};
                    var bookingEnquiry = (BookingRequest) args["bookingRequest"];

                    var rooms = bookingEnquiry.Room == "No preference" ? (from room in bookingEnquiry.AvailableRooms select room.UserPrincipalName) : new[] {bookingEnquiry.AvailableRooms.FirstOrDefault(x => x.DisplayName == bookingEnquiry.Room).UserPrincipalName};

                    // 15. query Office 365 Graph for availability
                    var meetings = await MicrosoftGraphExtensions.GetMicrosoftGraphFindMeeting(
                        dc.Context.Services.Get<ConversationAuthToken>(AzureAdAuthMiddleware.AUTH_TOKEN_KEY).AccessToken,
                        bookingEnquiry.Start.Value,
                        bookingEnquiry.Start.Value + XmlConvert.ToTimeSpan(bookingEnquiry.MeetingDuration),
                        bookingEnquiry.MeetingDuration,
                        rooms.ToArray());

                    var bookingChoices = new List<(string, object)>();
                    foreach (var suggestion in meetings.MeetingTimeSuggestions)
                    {
                        foreach (var location in suggestion.Locations)
                        {
                            var display = $"{bookingEnquiry.AvailableRooms.FirstOrDefault(x => x.UserPrincipalName.ToLower() == location.LocationEmailAddress.ToLower()).DisplayName}: {DateTime.Parse(suggestion.MeetingTimeSlot.Start.DateTime).DayOfWeek} {DateTime.Parse(suggestion.MeetingTimeSlot.Start.DateTime).ToString("HH:mm")} - {DateTime.Parse(suggestion.MeetingTimeSlot.End.DateTime).ToString("HH:mm")}";
                            var value = new {start = DateTime.Parse(suggestion.MeetingTimeSlot.Start.DateTime), end = DateTime.Parse(suggestion.MeetingTimeSlot.End.DateTime), roomEmail = location.LocationEmailAddress};
                            bookingChoices.Add((display, JsonConvert.SerializeObject(value)));
                        }
                    }

                    if (bookingChoices.Count == 0)
                    {
                        await dc.Context.SendActivity(dc.Context.Activity.CreateReply("Couldn't find any availability for that timeslot. A future improvement might be to widen the search by location or timeslots"));
                        dc.EndAll();
                    }
                    else
                    {
                        // 16. Present the choices back to the user as an adaptive card (see adaptivecards.io)
                        var activity = dc.Context.Activity.CreateReply();
                        activity.AddAdaptiveCardChoiceForm(bookingChoices.ToArray());
                        await dc.Context.SendActivity(activity);
                    }
                },
                async (dc, args, next) =>
                {
                    if (args["Activity"] is Activity activity && activity.Value != null && ((dynamic) activity.Value).chosenRoom is JValue chosenRoom)
                    {
                        dynamic requestedBooking = JsonConvert.DeserializeObject<ExpandoObject>((string) chosenRoom.Value);
                        // 17. Finally book the meeting
                        var meetingWebLink = await MicrosoftGraphExtensions.BookMicrosoftGraphMeeting(dc.Context.Services.Get<ConversationAuthToken>(AzureAdAuthMiddleware.AUTH_TOKEN_KEY).AccessToken,
                            "Booked meeting", requestedBooking.roomEmail, requestedBooking.start, requestedBooking.end);

                        var confirmation = activity.CreateReply();
                        // 18. And send back a confirmation card
                        CardExtensions.AddAdaptiveCardRoomConfirmationAttachment(confirmation, requestedBooking.roomEmail, $"{requestedBooking.start.DayOfWeek} {requestedBooking.start.ToString("HH:mm")}", $"{requestedBooking.start.DayOfWeek} {requestedBooking.end.ToString("HH:mm")}", meetingWebLink);
                        await dc.Context.SendActivity(confirmation);
                        await dc.End();
                    }
                    else
                    {
                        await dc.Begin(Id, dc.ActiveDialog.State);
                    }
                }
            });
        }

        public static string Id => "searchGraphDialog";
    }
}